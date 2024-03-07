using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-relink")]
    public async Task ComponentRelink()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRelink), Context.Interaction.User.Id);

        EmbedBuilder eb = new();
        eb.WithTitle("重新连接");
        eb.WithColor(Color.Blue);
        eb.WithDescription("如果您已经注册了 Mare 帐户，但无法访问之前的 Discord 帐户，请使用此选项。" + Environment.NewLine + Environment.NewLine
            + "- 准备好您的石之家 UID (例如 10000000)" + Environment.NewLine
            + "  - 注册需要您使用生成的验证码修改您的石之家个人资料" + Environment.NewLine
            + "- 不要在移动设备上使用此功能，因为您需要能够复制生成的密钥");
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("开始重新连接", "wizard-relink-start", ButtonStyle.Primary, emote: new Emoji("🔗"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-relink-start")]
    public async Task ComponentRelinkStart()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRelinkStart), Context.Interaction.User.Id);

        using var db = GetDbContext();
        db.LodeStoneAuth.RemoveRange(db.LodeStoneAuth.Where(u => u.DiscordId == Context.User.Id));
        _botServices.DiscordVerifiedUsers.TryRemove(Context.User.Id, out _);
        _botServices.DiscordRelinkLodestoneMapping.TryRemove(Context.User.Id, out _);
        await db.SaveChangesAsync().ConfigureAwait(false);

        await RespondWithModalAsync<LodestoneModal>("wizard-relink-lodestone-modal").ConfigureAwait(false);
    }

    [ModalInteraction("wizard-relink-lodestone-modal")]
    public async Task ModalRelink(LodestoneModal lodestoneModal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{url}", nameof(ModalRelink), Context.Interaction.User.Id, lodestoneModal.LodestoneUrl);

        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        var result = await HandleRelinkModalAsync(eb, lodestoneModal).ConfigureAwait(false);
        ComponentBuilder cb = new();
        cb.WithButton("取消", "wizard-relink", ButtonStyle.Secondary, emote: new Emoji("❌"));
        if (result.Success) cb.WithButton("验证", "wizard-relink-verify:" + result.LodestoneAuth + "," + result.UID, ButtonStyle.Primary, emote: new Emoji("✅"));
        else cb.WithButton("重试", "wizard-relink-start", ButtonStyle.Primary, emote: new Emoji("🔁"));
        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-relink-verify:*,*")]
    public async Task ComponentRelinkVerify(string verificationCode, string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}:{verificationCode}", nameof(ComponentRelinkVerify), Context.Interaction.User.Id, uid, verificationCode);


        _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Func<DiscordBotServices, Task>>(Context.User.Id,
            (services) => HandleVerifyRelinkAsync(Context.User.Id, verificationCode, services)));
        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        eb.WithColor(Color.Purple);
        cb.WithButton("取消", "wizard-relink", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("检查", "wizard-relink-verify-check:" + verificationCode + "," + uid, ButtonStyle.Primary, emote: new Emoji("❓"));
        eb.WithTitle("重新连接验证待定");
        eb.WithDescription("请等待机器人验证您的注册。" + Environment.NewLine
            + "按“检查”检查验证是否已处理" + Environment.NewLine + Environment.NewLine
            + "__这不会自动前进，您需要按“检查”按钮。__");
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-relink-verify-check:*,*")]
    public async Task ComponentRelinkVerifyCheck(string verificationCode, string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}:{verificationCode}", nameof(ComponentRelinkVerifyCheck), Context.Interaction.User.Id, uid, verificationCode);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        bool stillEnqueued = _botServices.VerificationQueue.Any(k => k.Key == Context.User.Id);
        bool verificationRan = _botServices.DiscordVerifiedUsers.TryGetValue(Context.User.Id, out bool verified);
        if (!verificationRan)
        {
            if (stillEnqueued)
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("您的重新链接验证仍在等待中");
                eb.WithDescription("请重试并在几秒钟后单击“检查”");
                cb.WithButton("取消", "wizard-relink", ButtonStyle.Secondary, emote: new Emoji("❌"));
                cb.WithButton("检查", "wizard-relink-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
            }
            else
            {
                eb.WithColor(Color.Red);
                eb.WithTitle("Something went wrong");
                eb.WithDescription("Your relink verification was processed but did not arrive properly. Please try to start the relink process from the start.");
                cb.WithButton("Restart", "wizard-relink", ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }
        else
        {
            if (verified)
            {
                eb.WithColor(Color.Green);
                using var db = _services.CreateScope().ServiceProvider.GetRequiredService<MareDbContext>();
                var (_, key) = await HandleRelinkUser(db, uid).ConfigureAwait(false);
                eb.WithTitle($"重新链接成功，您的UID又回来了: {uid}");
                eb.WithDescription("这是您的私人密钥。 不要与任何人共享此私人密钥。 **如果你失去了它，就永远失去了。**"
                                             + Environment.NewLine + Environment.NewLine
                                             + $"**{key}**"
                                             + Environment.NewLine + Environment.NewLine
                                             + "在 Mare Synchronos 中输入此密钥并点击“保存”以连接到该服务。"
                                             + Environment.NewLine
                                             + "玩得开心。");
                AddHome(cb);
            }
            else
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("重新链接失败");
                eb.WithDescription("机器人无法在您的石之家个人简介中找到所需的验证码。" + Environment.NewLine + Environment.NewLine
                    + "请重新启动您的重新链接过程，确保保存您的个人简介。" + Environment.NewLine + Environment.NewLine
                    + "机器人正在寻找的代码是" + Environment.NewLine + Environment.NewLine
                    + "**" + verificationCode + "**");
                cb.WithButton("取消", "wizard-relink", emote: new Emoji("❌"));
                cb.WithButton("重试", "wizard-relink-verify:" + verificationCode + "," + uid, ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task<(bool Success, string LodestoneAuth, string UID)> HandleRelinkModalAsync(EmbedBuilder embed, LodestoneModal arg)
    {
        ulong userId = Context.User.Id;

        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("无效的石之家 UID");
            embed.WithDescription("石之家 UID 无效。 它应该具有以下格式：" + Environment.NewLine
                + "10000000");
            return (false, string.Empty, string.Empty);
        }
        // check if userid is already in db
        using var scope = _services.CreateScope();

        var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        // check if discord id or lodestone id is banned
        if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
        {
            embed.WithTitle("Illegal operation");
            embed.WithDescription("Your account is banned");
            return (false, string.Empty, string.Empty);
        }

        if (!db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
        {
            // character already in db
            embed.WithTitle("Impossible operation");
            embed.WithDescription("This lodestone character does not exist in the database.");
            return (false, string.Empty, string.Empty);
        }

        var expectedUser = await db.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.HashedLodestoneId == hashedLodestoneId).ConfigureAwait(false);

        string lodestoneAuth = await GenerateLodestoneAuth(Context.User.Id, hashedLodestoneId, db).ConfigureAwait(false);
        // check if lodestone id is already in db
        embed.WithTitle("验证您的角色来重新连接");
        embed.WithDescription("将以下密钥添加到您的角色个人简介中：https://ff14risingstones.web.sdo.com/pc/index.html#/me/settings/main"
                            + Environment.NewLine + Environment.NewLine
                            + $"**{lodestoneAuth}**"
                            + Environment.NewLine + Environment.NewLine
                            + $"**! 这不是您在 MARE 中需要输入的密钥 !**"
                            + Environment.NewLine
                            + "__验证后，您可以从您的个人简介中删除该条目。__"
                            + Environment.NewLine + Environment.NewLine
                            + "验证将在大约 15 分钟后过期。 若验证不通过，则注册无效，需重新注册。");
        _botServices.DiscordRelinkLodestoneMapping[Context.User.Id] = lodestoneId.ToString();

        return (true, lodestoneAuth, expectedUser.User.UID);
    }

    private async Task HandleVerifyRelinkAsync(ulong userid, string authString, DiscordBotServices services)
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
        if (services.DiscordRelinkLodestoneMapping.ContainsKey(userid))
        {
            // var randomServer = _botServices.LodestoneServers[random.Next(_botServices.LodestoneServers.Length)];
            var response = await req.GetAsync($"https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={services.DiscordRelinkLodestoneMapping[userid]}&part_id=&orderBy=time&page=1&limit=20").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (content.Contains(authString))
                {
                    services.DiscordVerifiedUsers[userid] = true;
                    services.DiscordRelinkLodestoneMapping.TryRemove(userid, out _);
                }
                else
                {
                    services.DiscordVerifiedUsers[userid] = false;
                }
            }
        }
    }

    private async Task<(string, string)> HandleRelinkUser(MareDbContext db, string uid)
    {
        var oldLodestoneAuth = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid && u.DiscordId != Context.User.Id).ConfigureAwait(false);
        var newLodestoneAuth = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false);

        var user = oldLodestoneAuth.User;

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        var auth = new Auth()
        {
            HashedKey = StringUtils.Sha256String(computedHash),
            User = user,
        };

        var previousAuth = await db.Auth.SingleOrDefaultAsync(u => u.UserUID == user.UID).ConfigureAwait(false);
        if (previousAuth != null)
        {
            db.Remove(previousAuth);
        }

        newLodestoneAuth.LodestoneAuthString = null;
        newLodestoneAuth.StartedAt = null;
        newLodestoneAuth.User = user;
        db.Update(newLodestoneAuth);
        db.Remove(oldLodestoneAuth);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        _botServices.Logger.LogInformation("User relinked: {userUID}", user.UID);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.UID, computedHash);
    }
}
