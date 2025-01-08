using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Discord.Rest;
using Discord.WebSocket;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-register")]
    public async Task ComponentRegister()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRegister), Context.Interaction.User.Id);

        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("开始注册");
        eb.WithDescription("在这里，您可以开始使用此 Discord 的 Mare Synchronos 服务器进行注册。" + Environment.NewLine + Environment.NewLine
            + "- 准备好您的石之家 UID (例如 10000000)" + Environment.NewLine
            + "  - 注册需要您使用生成的验证码修改您的石之家个人资料" + Environment.NewLine
            + "- 不要在移动设备上使用此功能，因为您需要能够复制生成的密钥" + Environment.NewLine
            + "# 仔细阅读注册流程. 再~慢~一~点~.");
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("开始注册", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🌒"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-start")]
    public async Task ComponentRegisterStart()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRegisterStart), Context.Interaction.User.Id);

        using var db = await GetDbContext().ConfigureAwait(false);
        var entry = await db.LodeStoneAuth.SingleOrDefaultAsync(u => u.DiscordId == Context.User.Id && u.StartedAt != null).ConfigureAwait(false);
        if (entry != null)
        {
            db.LodeStoneAuth.Remove(entry);
        }
        _botServices.DiscordLodestoneMapping.TryRemove(Context.User.Id, out _);
        _botServices.DiscordVerifiedUsers.TryRemove(Context.User.Id, out _);

        await db.SaveChangesAsync().ConfigureAwait(false);

        await RespondWithModalAsync<LodestoneModal>("wizard-register-lodestone-modal").ConfigureAwait(false);
    }

    [ModalInteraction("wizard-register-lodestone-modal")]
    public async Task ModalRegister(LodestoneModal lodestoneModal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{lodestone}", nameof(ModalRegister), Context.Interaction.User.Id, lodestoneModal.LodestoneUrl);

        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        var success = await HandleRegisterModalAsync(eb, lodestoneModal).ConfigureAwait(false);
        ComponentBuilder cb = new();
        cb.WithButton("取消", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        if (success.Item1) cb.WithButton("验证", "wizard-register-verify:" + success.Item2, ButtonStyle.Primary, emote: new Emoji("✅"));
        else cb.WithButton("重试", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🔁"));
        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify:*")]
    public async Task ComponentRegisterVerify(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{verificationcode}", nameof(ComponentRegisterVerify), Context.Interaction.User.Id, verificationCode);

        _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Func<DiscordBotServices, Task>>(Context.User.Id,
            (service) => HandleVerifyAsync(Context.User.Id, verificationCode, service)));
        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        eb.WithColor(Color.Purple);
        cb.WithButton("取消", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("检查", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
        eb.WithTitle("待验证");
        eb.WithDescription("请等待机器人验证您的注册。" + Environment.NewLine
            + "按“检查”检查验证是否已处理" + Environment.NewLine + Environment.NewLine
            + "__这一步骤不会自动前进，您需要点击“检查”按钮。__");
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify-check:*")]
    public async Task ComponentRegisterVerifyCheck(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ComponentRegisterVerifyCheck), Context.Interaction.User.Id, verificationCode);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        bool stillEnqueued = _botServices.VerificationQueue.Any(k => k.Key == Context.User.Id);
        bool verificationRan = _botServices.DiscordVerifiedUsers.TryGetValue(Context.User.Id, out bool verified);
        bool registerSuccess = false;
        if (!verificationRan)
        {
            if (stillEnqueued)
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("您的验证仍在等待中");
                eb.WithDescription("请重试并在几秒钟后单击“检查”");
                cb.WithButton("取消", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
                cb.WithButton("检查", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
            }
            else
            {
                eb.WithColor(Color.Red);
                eb.WithTitle("发生了错误");
                eb.WithDescription("您的验证已处理，但未正确完成。 请尝试从头开始注册。");
                cb.WithButton("重试", "wizard-register", ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }
        else
        {
            if (verified)
            {
                eb.WithColor(Color.Green);
                using var db = await GetDbContext().ConfigureAwait(false);
                var (uid, key) = await HandleAddUser(db).ConfigureAwait(false);
                eb.WithTitle($"注册成功，您的UID：{uid}");
                eb.WithDescription("这是您的私人密钥。 不要与任何人共享此私人密钥。 **如果你失去了它，它就永远失去了。**"
                                             + Environment.NewLine + Environment.NewLine
                                             + $"**{key}**"
                                             + Environment.NewLine + Environment.NewLine
                                             + "在 Mare Synchronos 中输入此密钥并点击“保存”以连接到该服务。"
                                             + Environment.NewLine
                                             + "__注意: 密钥仅包括英文 ABCDEF 和数字 0 - 9.__"
                                             + Environment.NewLine
                                             + " __注意: 建议使用OAuth2登录,密钥登录可能在未来会被放弃支持.__"
                                             + Environment.NewLine
                                             + "您应该尽快连接，以免被自动清理。"
                                             + Environment.NewLine
                                             + "玩得开心。");
                AddHome(cb);
                registerSuccess = true;
            }
            else
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("验证注册失败");
                eb.WithDescription("机器人无法在您的石之家个人资料中找到所需的验证码。" 
                    + Environment.NewLine + Environment.NewLine
                    + "请重新启动您的验证过程，并确保提交您的个人资料 _两次_ 以便正确保存。" 
                    + Environment.NewLine + Environment.NewLine
                    + "**请确保你的个人资料对所有人公开，否则机器人将无法正常读取。**" 
                    + Environment.NewLine + Environment.NewLine
                    + "## 你 __必须__ 输入以下代码让机器人查询:" 
                    + Environment.NewLine + Environment.NewLine
                    + "**" + verificationCode + "**");
                cb.WithButton("取消", "wizard-register", emote: new Emoji("❌"));
                cb.WithButton("重试", "wizard-register-verify:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
        if (registerSuccess)
            await _botServices.AddRegisteredRoleAsync(Context.Interaction.User).ConfigureAwait(false);
    }

    private async Task<(bool, string)> HandleRegisterModalAsync(EmbedBuilder embed, LodestoneModal arg)
    {
        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("不正确的 UID");
            embed.WithDescription("石之家的 UID 格式错误，它应当长成这样:" + Environment.NewLine
                + "10000000");
            return (false, string.Empty);
        }

        // check if userid is already in db
        var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

        using var db = await GetDbContext().ConfigureAwait(false);

        // check if discord id or lodestone id is banned
        if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
        {
            embed.WithDescription("This account is banned");
            return (false, string.Empty);
        }

        if (db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
        {
            // character already in db
            embed.WithDescription("该石之家角色已存在于数据库中。 如果您想将此角色附加到您当前的 Discord 帐户，请使用重新连接。");
            return (false, string.Empty);
        }

        string lodestoneAuth = await GenerateLodestoneAuth(Context.User.Id, hashedLodestoneId, db).ConfigureAwait(false);
        // check if lodestone id is already in db
        embed.WithTitle("验证您的角色");
        embed.WithDescription("将以下密钥添加到您的角色个人简介中：https://ff14risingstones.web.sdo.com/pc/index.html#/me/settings/main"
                              + Environment.NewLine
                              + "__NOTE: If the link does not lead you to your character edit profile page, you need to log in and set up your privacy settings!__"
                              + Environment.NewLine + Environment.NewLine
                              + $"**{lodestoneAuth}**"
                              + Environment.NewLine + Environment.NewLine
                              + $"**! 这不是您在 MARE 中需要输入的密钥 !**"
                              + Environment.NewLine + Environment.NewLine
                              + "添加并保存后，使用下面的按钮验证并完成注册并接收用于 Mare Synchronos 的密钥。"
                              + Environment.NewLine
                              + "__验证后，您可以从您的个人简介中删除该条目。__"
                              + Environment.NewLine + Environment.NewLine
                              + "验证将在大约 15 分钟后过期。 若验证不通过，则注册无效，需重新注册。");
        _botServices.DiscordLodestoneMapping[Context.User.Id] = lodestoneId.ToString();

        return (true, lodestoneAuth);
    }

    private async Task HandleVerifyAsync(ulong userid, string authString, DiscordBotServices services)
    {
        var req = new HttpClient();
        var cookie = GetSZJCookie();
        if (!string.IsNullOrEmpty(cookie))
        {
            // req.DefaultRequestHeaders.Add("Cookie", cookie);
            req.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _botServices.Logger.LogInformation("Set bot cookie to {botCookie}", cookie);
        }
        else
        {
            _botServices.Logger.LogError("Cannot get cookie for bot service");
        }

        services.DiscordVerifiedUsers.Remove(userid, out _);
        if (services.DiscordLodestoneMapping.ContainsKey(userid))
        {
            // var randomServer = services.LodestoneServers[random.Next(services.LodestoneServers.Length)];
            var url = $"https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={services.DiscordLodestoneMapping[userid]}&part_id=&orderBy=time&page=1&limit=20";
            var response = await req.GetAsync(url).ConfigureAwait(false);
            _logger.LogInformation("Verifying {userid} with URL {url}", userid, url);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(authString))
                {
                    services.DiscordVerifiedUsers[userid] = true;
                    _logger.LogInformation("Verified {userid} from lodestone {lodestone}", userid, services.DiscordLodestoneMapping[userid]);
                    await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Success.").ConfigureAwait(false);
                    services.DiscordLodestoneMapping.TryRemove(userid, out _);
                }
                else
                {
                    services.DiscordVerifiedUsers[userid] = false;
                    _logger.LogInformation("Could not verify {userid} from lodestone {lodestone}, did not find authString: {authString}, status code was: {code}",
                        userid, services.DiscordLodestoneMapping[userid], authString, response.StatusCode);
                    await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Failed: No Authstring ({authString}). (<{url}>)").ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogWarning("Could not verify {userid}, HttpStatusCode: {code}", userid, response.StatusCode);
                await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Failed: HttpStatusCode {response.StatusCode}. (<{url}>)").ConfigureAwait(false);
            }
        }
    }

    private async Task<(string, string)> HandleAddUser(MareDbContext db)
    {
        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == Context.User.Id);

        var user = new User();

        var hasValidUid = false;
        while (!hasValidUid)
        {
            var uid = StringUtils.GenerateRandomString(10);
            if (db.Users.Any(u => u.UID == uid || u.Alias == uid)) continue;
            user.UID = uid;
            hasValidUid = true;
        }

        // make the first registered user on the service to admin
        if (!await db.Users.AnyAsync().ConfigureAwait(false))
        {
            user.IsAdmin = true;
        }

        user.LastLoggedIn = DateTime.UtcNow;

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        string hashedKey = StringUtils.Sha256String(computedHash);
        var auth = new Auth()
        {
            HashedKey = hashedKey,
            User = user,
        };

        await db.Users.AddAsync(user).ConfigureAwait(false);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        lodestoneAuth.StartedAt = null;
        lodestoneAuth.User = user;
        lodestoneAuth.LodestoneAuthString = null;

        await db.SaveChangesAsync().ConfigureAwait(false);

        _botServices.Logger.LogInformation("User registered: {userUID}:{hashedKey}", user.UID, hashedKey);

        await _botServices.LogToChannel($"{Context.User.Mention} REGISTER COMPLETE: => {user.UID}").ConfigureAwait(false);

        _botServices.DiscordVerifiedUsers.Remove(Context.User.Id, out _);

        return (user.UID, computedHash);
    }
}
