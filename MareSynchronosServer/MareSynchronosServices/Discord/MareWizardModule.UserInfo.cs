using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-userinfo")]
    public async Task ComponentUserinfo()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentUserinfo), Context.Interaction.User.Id);

        using var mareDb = GetDbContext();
        EmbedBuilder eb = new();
        eb.WithTitle("User Info");
        eb.WithColor(Color.Blue);
        eb.WithDescription("在这里你能看到你的Mare用户信息。" + Environment.NewLine
            + "用下面的选择框来选取需要查看信息的UID" + Environment.NewLine + Environment.NewLine
            + "- 1️⃣ 是你的主要账号/UID" + Environment.NewLine
            + "- 2️⃣ 是你所有的辅助UID" + Environment.NewLine
            + "如果你在使用个性 UID的话，原始的UID会在账号选项的第二行显示。");
        ComponentBuilder cb = new();
        await AddUserSelection(mareDb, cb, "wizard-userinfo-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-userinfo-select")]
    public async Task SelectionUserinfo(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionUserinfo), Context.Interaction.User.Id, uid);

        using var mareDb = GetDbContext();
        EmbedBuilder eb = new();
        eb.WithTitle($"用户信息: {uid}");
        await HandleUserInfo(eb, mareDb, uid).ConfigureAwait(false);
        eb.WithColor(Color.Green);
        ComponentBuilder cb = new();
        await AddUserSelection(mareDb, cb, "wizard-userinfo-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task HandleUserInfo(EmbedBuilder eb, MareDbContext db, string uid)
    {
        ulong userToCheckForDiscordId = Context.User.Id;

        var dbUser = await db.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);

        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var identity = await _connectionMultiplexer.GetDatabase().StringGetAsync("UID:" + dbUser.UID).ConfigureAwait(false);

        eb.WithDescription("这是你选中的UID的信息，你可以在下方菜单检查其他UID的信息，或者返回主菜单。" + Environment.NewLine);
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("个性 UID", dbUser.Alias);
        }
        eb.AddField("上一次在线(本地时间)", $"<t:{new DateTimeOffset(dbUser.LastLoggedIn.ToUniversalTime()).ToUnixTimeSeconds()}:f>");
        eb.AddField("目前是否在线", !string.IsNullOrEmpty(identity));

        eb.AddField("加入的同步贝数量", groupsJoined.Count);
        eb.AddField("拥有的同步贝数量", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(group.Alias))
            {
                eb.AddField("拥有的同步贝 " + group.GID + " ,个性 GID", group.Alias);
            }
            eb.AddField("拥有的同步贝 " + group.GID + " ,用户数量", syncShellUserCount);
        }
    }

}
