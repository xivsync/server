﻿using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-secondary")]
    public async Task ComponentSecondary()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentSecondary), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        var primaryUID = (await mareDb.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false)).User.UID;
        var secondaryUids = await mareDb.Auth.CountAsync(p => p.PrimaryUserUID == primaryUID).ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("Secondary UID");
        eb.WithDescription("你可以在这里创建Secondary UID " + Environment.NewLine + Environment.NewLine
            + "Secondary UID拥有完全分离的配对列表，加入的同步贝，显示的UID等等。" + Environment.NewLine
            + "如果你想双开游戏，或者给小号一个私密的身份，请在此创建Secondary UID。" + Environment.NewLine + Environment.NewLine
            + "__提示:__ 对于让小号使用Mare，创建Secondary UID _不是_ 必须的。" + Environment.NewLine + Environment.NewLine
            + $"你目前拥有 {secondaryUids} 个Secondary UID，最多可拥有的数量是20个。");
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("创建Secondary UID", "wizard-secondary-create:" + primaryUID, ButtonStyle.Primary, emote: new Emoji("2️⃣"), disabled: secondaryUids >= 20);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-secondary-create:*")]
    public async Task ComponentSecondaryCreate(string primaryUid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{primary}", nameof(ComponentSecondaryCreate), Context.Interaction.User.Id, primaryUid);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle("Secondary UID创建完成");
        eb.WithColor(Color.Green);
        ComponentBuilder cb = new();
        AddHome(cb);
        await HandleAddSecondary(mareDb, eb, primaryUid).ConfigureAwait(false);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    public async Task HandleAddSecondary(MareDbContext db, EmbedBuilder embed, string primaryUID)
    {
        User newUser = new()
        {
            IsAdmin = false,
            IsModerator = false,
            LastLoggedIn = DateTime.UtcNow,
        };

        var hasValidUid = false;
        while (!hasValidUid)
        {
            var uid = StringUtils.GenerateRandomString(10);
            if (await db.Users.AnyAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false)) continue;
            newUser.UID = uid;
            hasValidUid = true;
        }

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        var auth = new Auth()
        {
            HashedKey = StringUtils.Sha256String(computedHash),
            User = newUser,
            PrimaryUserUID = primaryUID
        };

        await db.Users.AddAsync(newUser).ConfigureAwait(false);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        await db.SaveChangesAsync().ConfigureAwait(false);

        embed.WithDescription("一个Secondary UID创建完成，将以下的信息输入Mare的服务设置页面。");
        embed.AddField("UID", newUser.UID);
        embed.AddField("同步密钥", computedHash);

        await _botServices.LogToChannel($"{Context.User.Mention} SECONDARY SUCCESS: {newUser.UID}").ConfigureAwait(false);
    }

}
