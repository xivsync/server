using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-recover")]
    public async Task ComponentRecover()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRecover), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("恢复");
        eb.WithDescription("如果你丢失了同步密钥，可以在此恢复。" + Environment.NewLine + Environment.NewLine
            + "## ⚠️ **一旦你获取了新秘钥，旧密钥将会失效. 如果你在多台电脑使用Mare需要在每一处都更新密钥.** ⚠️" + Environment.NewLine + Environment.NewLine
            + "下面的选择框来选取需要恢复的UID。" + Environment.NewLine + Environment.NewLine
            + "- 1️⃣ 是你的主要账号/UID" + Environment.NewLine
            + "- 2️⃣ 是你所有的辅助UID" + Environment.NewLine
            + "如果你在使用个性 UID的话，原始的UID会在账号选项的第二行显示。" + Environment.NewLine
            + "# 注意: 建议使用OAuth2登录而非密钥, 将在不久的未来取消密钥登录.");
        ComponentBuilder cb = new();
        await AddUserSelection(mareDb, cb, "wizard-recover-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-recover-select")]
    public async Task SelectionRecovery(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionRecovery), Context.Interaction.User.Id, uid);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Green);
        await HandleRecovery(mareDb, eb, uid).ConfigureAwait(false);
        ComponentBuilder cb = new();
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task HandleRecovery(MareDbContext db, EmbedBuilder embed, string uid)
    {
        string computedHash = string.Empty;
        Auth auth;
        var previousAuth = await db.Auth.Include(u => u.User).FirstOrDefaultAsync(u => u.UserUID == uid).ConfigureAwait(false);
        if (previousAuth != null)
        {
            db.Auth.Remove(previousAuth);
        }

        computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        string hashedKey = StringUtils.Sha256String(computedHash);
        auth = new Auth()
        {
            HashedKey = hashedKey,
            User = previousAuth.User,
            PrimaryUserUID = previousAuth.PrimaryUserUID
        };

        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        embed.WithTitle($"您的账号 {uid} 恢复成功。");
        embed.WithDescription("这里是你的同步密钥。不要与任何人分享它 **如果它丢失了，是无法恢复的。**"
                              + Environment.NewLine + Environment.NewLine
                              + "**__NOTE: Secret keys are considered legacy authentication. If you are using the suggested OAuth2 authentication, you do not need to use the Secret Key or recover ever again.__**"
                              + Environment.NewLine + Environment.NewLine
                              + $"||**`{computedHash}`**||"
                              + Environment.NewLine
                              + "__NOTE: The Secret Key only contains the letters ABCDEF and numbers 0 - 9.__"
                              + Environment.NewLine + Environment.NewLine
                              + "输入此同步密钥到Mare服务设置中并重新连接服务。");

        await db.Auth.AddAsync(auth).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        _botServices.Logger.LogInformation("User recovered: {userUID}:{hashedKey}", previousAuth.UserUID, hashedKey);
        await _botServices.LogToChannel($"{Context.User.Mention} RECOVER SUCCESS: {previousAuth.UserUID}").ConfigureAwait(false);
    }
}
