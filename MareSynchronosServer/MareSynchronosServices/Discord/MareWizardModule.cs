using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.RegularExpressions;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule : InteractionModuleBase
{
    private ILogger<MareModule> _logger;
    private DiscordBotServices _botServices;
    private IConfigurationService<ServerConfiguration> _mareClientConfigurationService;
    private IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private IConnectionMultiplexer _connectionMultiplexer;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private Random random = new();

    public MareWizardModule(ILogger<MareModule> logger, DiscordBotServices botServices,
        IConfigurationService<ServerConfiguration> mareClientConfigurationService,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _logger = logger;
        _botServices = botServices;
        _mareClientConfigurationService = mareClientConfigurationService;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
        _dbContextFactory = dbContextFactory;
    }

    [ComponentInteraction("wizard-captcha:*")]
    public async Task WizardCaptcha(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        if (_botServices.VerifiedCaptchaUsers.Contains(Context.Interaction.User.Id))
        {
            await StartWizard(true).ConfigureAwait(false);
            return;
        }

        EmbedBuilder eb = new();

        Random rnd = new Random();
        var correctButton = rnd.Next(4) + 1;
        string nthButtonText = correctButton switch
        {
            1 => "一",
            2 => "二",
            3 => "三",
            4 => "四",
            _ => "错误",
        };

        Emoji nthButtonEmoji = correctButton switch
        {
            1 => new Emoji("⬅️"),
            2 => new Emoji("🤖"),
            3 => new Emoji("‼️"),
            4 => new Emoji("✉️"),
            _ => "unknown",
        };

        eb.WithTitle("Mare Bot Services 验证");
        eb.WithDescription("机器人启动后您的首次使用需要先进行验证." + Environment.NewLine + Environment.NewLine
            + "本机器人 __需要__ embeds 功能来正常运行. 如要继续,请保证你的 Embed 功能已启用." + Environment.NewLine
            + $"## 请点击下方 __第 **{nthButtonText}** 个按钮 ({nthButtonEmoji}).__");
        eb.WithColor(Color.LightOrange);

        int incorrectButtonHighlight = 1;
        do
        {
            incorrectButtonHighlight = rnd.Next(4) + 1;
        }
        while (incorrectButtonHighlight == correctButton);

        ComponentBuilder cb = new();
        cb.WithButton("使用", correctButton == 1 ? "wizard-home:false" : "wizard-captcha-fail:1", emote: new Emoji("⬅️"), style: incorrectButtonHighlight == 1 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("本机器人", correctButton == 2 ? "wizard-home:false" : "wizard-captcha-fail:2", emote: new Emoji("🤖"), style: incorrectButtonHighlight == 2 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("需要启用", correctButton == 3 ? "wizard-home:false" : "wizard-captcha-fail:3", emote: new Emoji("‼️"), style: incorrectButtonHighlight == 3 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("Embeds", correctButton == 4 ? "wizard-home:false" : "wizard-captcha-fail:4", emote: new Emoji("✉️"), style: incorrectButtonHighlight == 4 ? ButtonStyle.Primary : ButtonStyle.Secondary);

        await InitOrUpdateInteraction(init, eb, cb).ConfigureAwait(false);
    }

    private async Task InitOrUpdateInteraction(bool init, EmbedBuilder eb, ComponentBuilder cb)
    {
        if (init)
        {
            await RespondAsync(embed: eb.Build(), components: cb.Build(), ephemeral: true).ConfigureAwait(false);
            var resp = await GetOriginalResponseAsync().ConfigureAwait(false);
            _botServices.ValidInteractions[Context.User.Id] = resp.Id;
            _logger.LogInformation("Init Msg: {id}", resp.Id);
        }
        else
        {
            await ModifyInteraction(eb, cb).ConfigureAwait(false);
        }
    }

    [ComponentInteraction("wizard-captcha-fail:*")]
    public async Task WizardCaptchaFail(int button)
    {
        ComponentBuilder cb = new();
        cb.WithButton("重试", "wizard-captcha:false", emote: new Emoji("↩️"));
        await ((Context.Interaction) as IComponentInteraction).UpdateAsync(m =>
        {
            m.Embed = null;
            m.Content = "你点击了错误的按钮. 你可能关闭了 embeds 功能. 打开 (用户设置 -> 聊天 -> \"显示嵌入并预览黏贴在聊天中的网站链接\") 并重试.";
            m.Components = cb.Build();
        }).ConfigureAwait(false);

        await _botServices.LogToChannel($"{Context.User.Mention} FAILED CAPTCHA").ConfigureAwait(false);
    }


    [ComponentInteraction("wizard-home:*")]
    public async Task StartWizard(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        if (!_botServices.VerifiedCaptchaUsers.Contains(Context.Interaction.User.Id))
            _botServices.VerifiedCaptchaUsers.Add(Context.Interaction.User.Id);

        _logger.LogInformation("{method}:{userId}", nameof(StartWizard), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        bool hasAccount = await mareDb.LodeStoneAuth.AnyAsync(u => u.DiscordId == Context.User.Id && u.StartedAt == null).ConfigureAwait(false);

        //if (init)
        {
            bool isBanned = await mareDb.BannedRegistrations.AnyAsync(u => u.DiscordIdOrLodestoneAuth == Context.User.Id.ToString()).ConfigureAwait(false);

            if (isBanned)
            {
                EmbedBuilder ebBanned = new();
                ebBanned.WithTitle("你已被本服务器封禁");
                ebBanned.WithDescription("该Discord账号已被封禁");
                await RespondAsync(embed: ebBanned.Build(), ephemeral: true).ConfigureAwait(false);
                _logger.LogInformation("Banned user interacted {method}:{userId}", nameof(StartWizard), Context.Interaction.User.Id);
                return;
            }
        }
#if !DEBUG
        bool isInAprilFoolsMode = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleAprilFools2024), null) != null
            && DateTime.UtcNow.Month == 4 && DateTime.UtcNow.Day == 1 && DateTime.UtcNow.Year == 2024 && DateTime.UtcNow.Hour >= 10;
#elif DEBUG
        bool isInAprilFoolsMode = true;
#endif

        EmbedBuilder eb = new();
        eb.WithTitle("欢迎使用本服务器的 Mare Synchronos 服务机器人");
        eb.WithDescription("你可以做这些事情:" + Environment.NewLine + Environment.NewLine
            + (!hasAccount ? string.Empty : ("- 检查你的账号状态 点击 \"ℹ️ 用户信息\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- 注册一个新的 Mare 账号 点击 \"🌒 注册\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- 如果丢失了同步密钥 点击 \"🏥 恢复\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- 如果你更换了你的 Discord 账号 点击 \"🔗 重新连接\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- 创建一个小号 Mare UID 点击 \"2️⃣ 辅助UID\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- 设置个性 Mare UID 点击 \"💅 个性 UID\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- 删除你的大号或者小号 点击 \"⚠️ 删除\""))
            );
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        if (!hasAccount)
        {
            cb.WithButton("注册", "wizard-register", ButtonStyle.Primary, new Emoji("🌒"));
            cb.WithButton("重新连接", "wizard-relink", ButtonStyle.Secondary, new Emoji("🔗"));
        }
        else
        {
            cb.WithButton("用户信息", "wizard-userinfo", ButtonStyle.Secondary, new Emoji("ℹ️"));
            //cb.WithButton("恢复", "wizard-recover", ButtonStyle.Secondary, new Emoji("🏥"));
            cb.WithButton("辅助UID", "wizard-secondary", ButtonStyle.Secondary, new Emoji("2️⃣"));
            cb.WithButton("个性 UID", "wizard-vanity", ButtonStyle.Secondary, new Emoji("💅"));
            cb.WithButton("删除", "wizard-delete", ButtonStyle.Danger, new Emoji("⚠️"));
            cb.WithButton("赞助", "wizard-support", ButtonStyle.Secondary, new Emoji("💎"));
        }

        await InitOrUpdateInteraction(init, eb, cb).ConfigureAwait(false);
    }

    public class VanityUidModal : IModal
    {
        public string Title => "设置个性 UID";

        [InputLabel("输入你想要设置的个性 UID")]
        [ModalTextInput("vanity_uid", TextInputStyle.Short, "2-15个字符，中文，下划线，短横线", 2, 15)]
        public string DesiredVanityUID { get; set; }
    }

    public class VanityGidModal : IModal
    {
        public string Title => "设置个性同步贝ID";

        [InputLabel("输入你想要设置的个性同步贝ID")]
        [ModalTextInput("vanity_gid", TextInputStyle.Short, "2-15个字符，中文，下划线，短横线", 2, 15)]
        public string DesiredVanityGID { get; set; }
    }

    public class ConfirmDeletionModal : IModal
    {
        public string Title => "确认账号删除";

        [InputLabel("输入全大写的 \"DELETE\"")]
        [ModalTextInput("confirmation", TextInputStyle.Short, "输入 DELETE")]
        public string Delete { get; set; }
    }

    private async Task<MareDbContext> GetDbContext()
    {
        return await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
    }

    private async Task<bool> ValidateInteraction()
    {
        if (Context.Interaction is not IComponentInteraction componentInteraction) return true;

        if (_botServices.ValidInteractions.TryGetValue(Context.User.Id, out ulong interactionId) && interactionId == componentInteraction.Message.Id)
        {
            return true;
        }

        EmbedBuilder eb = new();
        eb.WithTitle("会话已过期");
        eb.WithDescription("当前的会话已经过期, 请重新点击 \"开始\" 按钮开始一个新的对话." + Environment.NewLine + Environment.NewLine
            + "请使用最近一次的对话进行交互.");
        eb.WithColor(Color.Red);
        ComponentBuilder cb = new();
        await ModifyInteraction(eb, cb).ConfigureAwait(false);

        return false;
    }

    private void AddHome(ComponentBuilder cb)
    {
        cb.WithButton("返回主菜单", "wizard-home:false", ButtonStyle.Secondary, new Emoji("🏠"));
    }

    private async Task ModifyModalInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await (Context.Interaction as SocketModal).UpdateAsync(m =>
        {
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task ModifyInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await ((Context.Interaction) as IComponentInteraction).UpdateAsync(m =>
        {
            m.Content = null;
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task AddUserSelection(MareDbContext mareDb, ComponentBuilder cb, string customId)
    {
        var discordId = Context.User.Id;
        var existingAuth = await mareDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);
        if (existingAuth != null)
        {
            SelectMenuBuilder sb = new();
            sb.WithPlaceholder("选择一个UID");
            sb.WithCustomId(customId);
            var existingUids = await mareDb.Auth.Include(u => u.User).Where(u => u.UserUID == existingAuth.User.UID || u.PrimaryUserUID == existingAuth.User.UID)
                .OrderByDescending(u => u.PrimaryUser == null).ToListAsync().ConfigureAwait(false);
            foreach (var entry in existingUids)
            {
                sb.AddOption(string.IsNullOrEmpty(entry.User.Alias) ? entry.UserUID : entry.User.Alias,
                    entry.UserUID,
                    !string.IsNullOrEmpty(entry.User.Alias) ? entry.User.UID : null,
                    entry.PrimaryUserUID == null ? new Emoji("1️⃣") : new Emoji("2️⃣"));
            }
            cb.WithSelectMenu(sb);
        }
    }

    private async Task AddGroupSelection(MareDbContext db, ComponentBuilder cb, string customId)
    {
        var primary = (await db.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false)).User;
        var secondary = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == primary.UID).Select(u => u.User).ToListAsync().ConfigureAwait(false);
        var primaryGids = (await db.Groups.Include(u => u.Owner).Where(u => u.OwnerUID == primary.UID).ToListAsync().ConfigureAwait(false));
        var secondaryGids = (await db.Groups.Include(u => u.Owner).Where(u => secondary.Select(u => u.UID).Contains(u.OwnerUID)).ToListAsync().ConfigureAwait(false));
        SelectMenuBuilder gids = new();
        if (primaryGids.Any() || secondaryGids.Any())
        {
            foreach (var item in primaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("1️⃣"));
            }
            foreach (var item in secondaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("2️⃣"));
            }
            gids.WithCustomId(customId);
            gids.WithPlaceholder("选择一个同步贝");
            cb.WithSelectMenu(gids);
        }
    }

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, MareDbContext dbContext)
    {
        var auth = StringUtils.GenerateRandomString(12, "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz");
        LodeStoneAuth lsAuth = new LodeStoneAuth()
        {
            DiscordId = discordid,
            HashedLodestoneId = hashedLodestoneId,
            LodestoneAuthString = auth,
            StartedAt = DateTime.UtcNow
        };

        dbContext.Add(lsAuth);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return (auth);
    }

    private int? ParseCharacterIdFromLodestoneUrl(string lodestoneUrl)
    {
        // var regex = new Regex(@"https:\/\/(na|eu|de|fr|jp)\.finalfantasyxiv\.com\/lodestone\/character\/\d+");
        var regex = new Regex(@"^\d{8,}$");
        var matches = regex.Match(lodestoneUrl);
        var isLodestoneUrl = matches.Success;
        if (!isLodestoneUrl) return null;

        lodestoneUrl = matches.Groups[0].ToString();
        var stringId = lodestoneUrl;
        if (!int.TryParse(stringId, out int lodestoneId))
        {
            return null;
        }

        return lodestoneId;
    }

    private string GetSZJCookie()
    {
        return _mareServicesConfiguration.GetValueOrDefault(nameof(ServicesConfiguration.SZJCookie), string.Empty);
    }

}