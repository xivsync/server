using Discord.Interactions;
using Discord;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-vanity")]
    public async Task ComponentVanity()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentVanity), Context.Interaction.User.Id);

        StringBuilder sb = new();
        var user = await Context.Guild.GetUserAsync(Context.User.Id).ConfigureAwait(false);
        bool userIsInVanityRole = _botServices.VanityRoles.Keys.Any(u => user.RoleIds.Contains(u.Id)) || !_botServices.VanityRoles.Any();
        if (!userIsInVanityRole)
        {
            sb.AppendLine("你必须拥有以下身份组之一才能设置个性ID:");
            foreach (var role in _botServices.VanityRoles)
            {
                sb.Append("- ").Append(role.Key.Mention).Append(" (").Append(role.Value).AppendLine(")");
            }
        }
        else
        {
            sb.AppendLine("你的当前身份组允许设置个性ID.");
        }

        EmbedBuilder eb = new();
        eb.WithTitle("个性 UID");
        eb.WithDescription("你可以在这里设置个性ID" + Environment.NewLine
            + "个性ID可以改变别人在同步贝里显示的你的ID（或你的同步贝的ID）。" + Environment.NewLine + Environment.NewLine
            + sb.ToString());
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        AddHome(cb);
        if (userIsInVanityRole)
        {
            using var db = await GetDbContext().ConfigureAwait(false);
            await AddUserSelection(db, cb, "wizard-vanity-uid").ConfigureAwait(false);
            await AddGroupSelection(db, cb, "wizard-vanity-gid").ConfigureAwait(false);
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid")]
    public async Task SelectionVanityUid(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUid), Context.Interaction.User.Id, uid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var user = db.Users.Single(u => u.UID == uid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"为 {uid} 设置个性 UID");
        eb.WithDescription($"你即将更改 {uid} 的个性 UID" + Environment.NewLine + Environment.NewLine
            + "目前设置的个性 UID是: **" + (user.Alias == null ? "没有设置个性 UID" : user.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("设置个性 UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid-set:*")]
    public async Task SelectionVanityUidSet(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUidSet), Context.Interaction.User.Id, uid);

        await RespondWithModalAsync<VanityUidModal>("wizard-vanity-uid-modal:" + uid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-uid-modal:*")]
    public async Task ConfirmVanityUidModal(string uid, VanityUidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}:{vanity}", nameof(ConfirmVanityUidModal), Context.Interaction.User.Id, uid, modal.DesiredVanityUID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityUid = modal.DesiredVanityUID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Users.Any(u => u.UID == modal.DesiredVanityUID || u.Alias == modal.DesiredVanityUID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9\u4e00-\u9fa5]{2,15}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityUid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("不符合要求的个性 UID");
            eb.WithDescription("个性 UID必须是2到15位长度，并且只包含中文, 字母 A-Z, 数字 0-9, 短横线 (-) 以及下划线 (_)。");
            cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("选择另一个UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("个性 UID已被占用");
            eb.WithDescription($"个性 UID {desiredVanityUid} 已经被占用了。 请选择一个其他的个性 UID。");
            cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("选择另一个UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var user = await db.Users.SingleAsync(u => u.UID == uid).ConfigureAwait(false);
            user.Alias = desiredVanityUid;
            db.Update(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("成功设置个性 UID");
            eb.WithDescription($"您的UID \"{uid}\" 的个性 UID成功设置为 \"{desiredVanityUid}\"。" + Environment.NewLine + Environment.NewLine
                + "重新连接Mare服务器来使变更生效。");
            AddHome(cb);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid")]
    public async Task SelectionVanityGid(string gid)
    {
        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityGid), Context.Interaction.User.Id, gid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var group = db.Groups.Single(u => u.GID == gid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"为同步贝 {gid} 设置个性 GID");
        eb.WithDescription($"你即将更改 {gid} 的个性 GID" + Environment.NewLine + Environment.NewLine
            + "目前设置的个性 GID是: **" + (group.Alias == null ? "没有设置个性 GID" : group.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("设置个性 GID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid-set:*")]
    public async Task SelectionVanityGidSet(string gid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}", nameof(SelectionVanityGidSet), Context.Interaction.User.Id, gid);

        await RespondWithModalAsync<VanityGidModal>("wizard-vanity-gid-modal:" + gid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-gid-modal:*")]
    public async Task ConfirmVanityGidModal(string gid, VanityGidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}:{vanity}", nameof(ConfirmVanityGidModal), Context.Interaction.User.Id, gid, modal.DesiredVanityGID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityGid = modal.DesiredVanityGID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Groups.Any(u => u.GID == modal.DesiredVanityGID || u.Alias == modal.DesiredVanityGID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9\u4e00-\u9fa5]{2,15}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityGid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("不符合要求的个性 GID");
            eb.WithDescription("个性 GID 必须是2到15位长度，并且只包含中文, 字母 A-Z, 数字 0-9, 短横线 (-) 以及下划线 (_)。");
            cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("选择另一个GID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("个性 GID已被占用");
            eb.WithDescription($"个性 GID \"{desiredVanityGid}\" 已经被占用了。 请选择一个其他的个性 GID.");
            cb.WithButton("取消", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("选择另一个GID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var group = await db.Groups.SingleAsync(u => u.GID == gid).ConfigureAwait(false);
            group.Alias = desiredVanityGid;
            db.Update(group);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("成功设置个性 GID");
            eb.WithDescription($"您的同步贝 {gid} 的个性 GID 成功设置为 \"{desiredVanityGid}\"." + Environment.NewLine + Environment.NewLine
                + "重新连接Mare服务器来使变更生效。");
            AddHome(cb);
            await _botServices.LogToChannel($"{Context.User.Mention} VANITY GID SET: GID: {group.GID}, Vanity: {desiredVanityGid}").ConfigureAwait(false);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }
}
