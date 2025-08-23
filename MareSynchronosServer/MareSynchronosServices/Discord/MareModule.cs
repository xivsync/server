using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Services;
using StackExchange.Redis;
using MareSynchronos.API.Data.Enum;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServices.Discord;

public class MareModule : InteractionModuleBase
{
    private readonly ILogger<MareModule> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ServerTokenGenerator _serverTokenGenerator;

    public MareModule(ILogger<MareModule> logger, IServiceProvider services,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer, ServerTokenGenerator serverTokenGenerator)
    {
        _logger = logger;
        _services = services;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
        _serverTokenGenerator = serverTokenGenerator;
    }

    [SlashCommand("userinfo", "Show your user information")]
    public async Task UserInfo(
        [Summary("secondary_uid", "(Optional) Your secondary UID")] string? secondaryUid = null,
        [Summary("discord_user", "Admins only: Discord user to check")] IUser? discordUser = null,
        [Summary("uid", "Admins only: UID to check")] string? uid = null,
        [Summary("lodestone", "Admins only: Lodestone account to check")] string? lodestone = null,
        [Summary("name_with_world", "Admins only: Character (Name@World) to check")] string? nameWithWorld = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Interaction.User.Id, nameof(UserInfo));

        try
        {
            EmbedBuilder eb = new();

            eb = await HandleUserInfo(eb, Context.User.Id, secondaryUid, discordUser?.Id ?? null, uid, lodestone, nameWithWorld);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occurred");
            eb.WithDescription("Please report this bug: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("useradd", "Admins only: unconditionally add a user to the database")]
    public async Task UserAdd([Summary("desired_uid", "User UID")] string desiredUid)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Interaction.User.Id, nameof(UserAdd),
            string.Join(",", new[] { $"{nameof(desiredUid)}:{desiredUid}" }));

        try
        {
            var embed = await HandleUserAdd(desiredUid, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occurred");
            eb.WithDescription("Please report this bug: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("mod", "Admins only: adjust moderators")]
    public async Task Mod(
        [Summary("discord_user", "Admins only: target Discord user")] IUser discordUser = null,
        [Summary("arg", "Argument: add or remove")] string arg = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Interaction.User.Id, nameof(Mod),
            string.Join(",", new[] { $"{nameof(discordUser)}:{discordUser}", $"{nameof(arg)}:{arg}" }));

        try
        {
            var embed = await HandleMod(discordUser, arg, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("An error occurred");
            eb.WithDescription("Please report this bug: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task<Embed> HandleMod(IUser targetUser, string arg, ulong discordUserId)
    {
        var embed = new EmbedBuilder();

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        var target = (await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == targetUser.Id))?.User;

        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == discordUserId))?.User?.IsAdmin ?? true)
        {
            embed.WithTitle("Permission change failed");
            embed.WithDescription("Insufficient permissions");
        }
        else
        {
            if (target != null)
            {
                target.IsModerator = arg switch
                {
                    "add" => true,
                    "remove" => false,
                    _ => target.IsModerator,
                };
                db.Users.Update(target);
                embed.WithTitle("Updated user's permissions");
                embed.WithDescription($"Permissions for <@{targetUser.Id}> changed to: {(target.IsModerator ? "Moderator" : "Non-moderator")}");
            }
            else
            {
                embed.WithTitle("Permission change failed");
                embed.WithDescription("User not found");
            }

            await db.SaveChangesAsync();
        }

        return embed.Build();
    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("message", "Admins only: send a message to clients")]
    public async Task SendMessageToClients(
        [Summary("message", "Message to send")] string message,
        [Summary("severity", "Message severity")] MessageSeverity messageType = MessageSeverity.Information,
        [Summary("uid", "UID to send to")] string? uid = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{message}:{type}:{uid}", Context.Interaction.User.Id, nameof(SendMessageToClients), message, messageType, uid);

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == Context.Interaction.User.Id))?.User?.IsAdmin ?? true)
        {
            await RespondAsync("Insufficient permissions", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(uid) && !await db.Users.AnyAsync(u => u.UID == uid))
        {
            await RespondAsync("UID does not exist", ephemeral: true).ConfigureAwait(false);
            return;
        }

        try
        {
            using HttpClient c = new HttpClient();
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _serverTokenGenerator.Token);
            await c.PostAsJsonAsync(new Uri(_mareServicesConfiguration.GetValue<Uri>
                (nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage"), new ClientMessage(messageType, message, uid ?? string.Empty))
                .ConfigureAwait(false);

            var discordChannelForMessages = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForMessages), null);
            if (uid == null && discordChannelForMessages != null)
            {
                var discordChannel = await Context.Guild.GetChannelAsync(discordChannelForMessages.Value) as IMessageChannel;
                if (discordChannel != null)
                {
                    var embedColor = messageType switch
                    {
                        MessageSeverity.Information => Color.Blue,
                        MessageSeverity.Warning => new Color(255, 255, 0),
                        MessageSeverity.Error => Color.Red,
                        _ => Color.Blue
                    };

                    EmbedBuilder eb = new();
                    eb.WithTitle(messageType + " Server Information");
                    eb.WithColor(embedColor);
                    eb.WithDescription(message);

                    await discordChannel.SendMessageAsync(embed: eb.Build());
                }
            }

            await RespondAsync("Message sent", ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RespondAsync($"Failed to send message to {new Uri(_mareServicesConfiguration.GetValue<Uri>
                (nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage")}: " + ex.Message, ephemeral: true).ConfigureAwait(false);
        }
    }

    [SlashCommand("getinvite", "Get an invite code for a specific syncshell")]
    public async Task GetInvitePassword([Summary("gid", "Group GID / Vanity GID")] string gidoralias)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{gid}", Context.Interaction.User.Id, nameof(GetInvitePassword), gidoralias);

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        var lodeStoneAuth = await db.LodeStoneAuth.AsNoTracking().Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false);

        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Alias == gidoralias || x.GID == gidoralias)
            .ConfigureAwait(false);
        if (group == null || !group.InvitesEnabled)
        {
            await RespondAsync($"Syncshell '{gidoralias}' not found or invites are disabled.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (lodeStoneAuth.User.UID == group.OwnerUID || await db.GroupPairs
                .AnyAsync(x => x.GroupGID == group.GID && x.GroupUser.UID == lodeStoneAuth.User.UID && x.IsModerator)
                .ConfigureAwait(false))
        {
            var existingInvites = await db.GroupTempInvites.Where(g => g.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);

            bool hasValidInvite = false;
            string invite = string.Empty;
            string hashedInvite = string.Empty;
            while (!hasValidInvite)
            {
                invite = StringUtils.GenerateRandomString(10, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                hashedInvite = StringUtils.Sha256String(invite);
                if (existingInvites.Any(i => string.Equals(i.Invite, hashedInvite, StringComparison.Ordinal))) continue;
                hasValidInvite = true;
            }

            var GroupTempInvite = new GroupTempInvite()
            {
                ExpirationDate = DateTime.UtcNow.AddDays(1),
                GroupGID = group.GID,
                Invite = hashedInvite,
            };

            db.GroupTempInvites.Add(GroupTempInvite);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await RespondAsync($"Here is the invite code for syncshell `{gidoralias}`, valid for 24 hours: `{invite}`", ephemeral: true).ConfigureAwait(false);
        }
        else
        {
            await RespondAsync("Not enough permissions; please check your input.", ephemeral: true).ConfigureAwait(false);
        }
    }

    public async Task<Embed> HandleUserAdd(string desiredUid, ulong discordUserId)
    {
        var embed = new EmbedBuilder();

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == discordUserId))?.User?.IsAdmin ?? true)
        {
            embed.WithTitle("Failed to add user");
            embed.WithDescription("Insufficient permissions");
        }
        else if (db.Users.Any(u => u.UID == desiredUid || u.Alias == desiredUid))
        {
            embed.WithTitle("Failed to add user");
            embed.WithDescription("User already exists");
        }
        else
        {
            User newUser = new()
            {
                IsAdmin = false,
                IsModerator = false,
                LastLoggedIn = DateTime.UtcNow,
                UID = desiredUid,
            };

            var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
            var auth = new Auth()
            {
                HashedKey = StringUtils.Sha256String(computedHash),
                User = newUser,
            };

            await db.Users.AddAsync(newUser);
            await db.Auth.AddAsync(auth);

            await db.SaveChangesAsync();

            embed.WithTitle("Successfully added " + desiredUid);
            embed.WithDescription("Key: " + computedHash);
        }

        return embed.Build();
    }

    private async Task<EmbedBuilder> HandleUserInfo(EmbedBuilder eb, ulong id, string? secondaryUserUid = null, ulong? optionalUser = null, string? uid = null, string? lodestoneId = null, string? nameWithWorld = null)
    {
        bool showForSecondaryUser = secondaryUserUid != null;
        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var primaryUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);

        ulong userToCheckForDiscordId = id;

        if (primaryUser == null)
        {
            eb.WithTitle("Account not found");
            eb.WithDescription("No Mare account is linked to this Discord account");
            return eb;
        }

        bool isAdminCall = primaryUser.User.IsModerator || primaryUser.User.IsAdmin;

        if ((optionalUser != null || uid != null || lodestoneId != null || nameWithWorld != null) && !isAdminCall)
        {
            eb.WithTitle("Insufficient permissions");
            eb.WithDescription("Only administrators can view other users' information");
            return eb;
        }
        else if ((optionalUser != null || uid != null || lodestoneId != null || nameWithWorld != null) && isAdminCall)
        {
            LodeStoneAuth userInDb = null;
            if (optionalUser != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == optionalUser).ConfigureAwait(false);
            }
            else if (uid != null)
            {
                var primary = (await db.Auth.SingleOrDefaultAsync(u => (u.UserUID == uid || u.User.Alias == uid) && u.PrimaryUserUID != null))?.PrimaryUserUID ?? uid;// if secondary, find primary Discord mapping
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == primary || u.User.Alias == primary).ConfigureAwait(false);
            }
            else if (lodestoneId != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.HashedLodestoneId == StringUtils.Sha256String(lodestoneId)).ConfigureAwait(false);
            }
            else if (nameWithWorld != null)
            {
                var user = await db.Auth.AsNoTracking().SingleOrDefaultAsync(u => u.NameWithWorld == StringUtils.Sha256String(nameWithWorld)).ConfigureAwait(false);
                if (user != null)
                {
                    var targetUid = user.PrimaryUserUID ?? user.UserUID;
                    userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == targetUid).ConfigureAwait(false);
                }
            }

            if (userInDb == null)
            {
                eb.WithTitle("Account not found");
                eb.WithDescription("The requested account was not found");
                return eb;
            }

            userToCheckForDiscordId = userInDb.DiscordId;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == userToCheckForDiscordId).ConfigureAwait(false);
        var dbUser = lodestoneUser.User;
        if (showForSecondaryUser)
        {
            dbUser = (await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.PrimaryUserUID == dbUser.UID && u.UserUID == secondaryUserUid))?.User;
            if (dbUser == null)
            {
                eb.WithTitle("Secondary UID not found");
                eb.WithDescription($"Secondary UID {secondaryUserUid} does not exist under your primary UID {primaryUser.User.UID}.");
                return eb;
            }
        }

        var secondaryCheck = isAdminCall && uid != null && uid != dbUser?.UID && uid != dbUser.Alias; // admin asked for a secondary UID
        if (secondaryCheck)
        {
            dbUser = (await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid || u.User.Alias == uid))?.User; // show secondary info
        }

        var auth = await db.Auth.Include(u => u.PrimaryUser).SingleOrDefaultAsync(u => u.UserUID == dbUser.UID).ConfigureAwait(false);
        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var identity = await _connectionMultiplexer.GetDatabase().StringGetAsync("UID:" + dbUser.UID).ConfigureAwait(false);
        var online = string.IsNullOrEmpty(identity) ? string.Empty : dbUser.UID + Environment.NewLine;

        eb.WithTitle("User information");
        eb.WithDescription("Information for Discord user <@" + userToCheckForDiscordId + ">" + Environment.NewLine);
        eb.AddField("UID", dbUser.UID);
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("Vanity UID", dbUser.Alias);
        }
        if (showForSecondaryUser || secondaryCheck)
        {
            eb.AddField($"Primary UID for {dbUser.UID}:", auth.PrimaryUserUID);
        }
        else
        {
            var secondaryUIDs = await db.Auth.Where(p => p.PrimaryUserUID == dbUser.UID).Select(p => p.UserUID).ToListAsync();
            if (secondaryUIDs.Any())
            {
                eb.AddField("Secondary UIDs:", string.Join(Environment.NewLine, secondaryUIDs));
                foreach (var secondaryUiD in secondaryUIDs)
                {
                    if (await _connectionMultiplexer.GetDatabase().KeyExistsAsync("UID:" + secondaryUiD).ConfigureAwait(false))
                    {
                        online += secondaryUiD + Environment.NewLine;
                    }
                }
            }
        }
        eb.AddField("Last online (local time)", $"<t:{new DateTimeOffset(dbUser.LastLoggedIn.ToUniversalTime()).ToUnixTimeSeconds()}:f>");
        eb.AddField("Online UID(s)", string.IsNullOrEmpty(online) ? "None" : online);
        // eb.AddField("Sync key hash", auth.HashedKey);
        eb.AddField("Joined syncshells", groupsJoined.Count);
        eb.AddField("Owned syncshells", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(group.Alias))
            {
                eb.AddField("Owned syncshell " + group.GID + " — vanity GID", group.Alias);
            }
            eb.AddField("Owned syncshell " + group.GID + " — member count", syncShellUserCount);
        }

        if (isAdminCall && !string.IsNullOrEmpty(identity))
        {
            eb.AddField("Online character ID", identity.ToString().Trim('"'));
        }

        if (isAdminCall && auth.CharaIds is not null)
        {
            eb.AddField("Former character ID count", auth.CharaIds.Count);
            if (auth.CharaIds.Count > 0)
            {
                eb.AddField("Former character IDs", string.Join(Environment.NewLine, auth.CharaIds.Take(5)));
            }
        }

        if (isAdminCall && !string.IsNullOrEmpty(lodestoneUser.HashedLodestoneId))
        {
            eb.AddField("Lodestone identifier", lodestoneUser.HashedLodestoneId);
        }

        if (isAdminCall && !string.IsNullOrEmpty(nameWithWorld))
        {
            eb.AddField($"Lookup `{nameWithWorld}`", auth.NameWithWorld);
        }

        return eb;
    }

    [SlashCommand("banuser", "Ban user")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task BanUser(
        [Summary("uid", "User UID")] string uid,
        [Summary("reason", "Ban reason")] string? reason = null)
    {

        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();
        var user = await dbContext.Auth.SingleAsync(u => u.UserUID == uid).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(user.PrimaryUserUID))
        {
            var primaryId = user.PrimaryUserUID;
            user = await dbContext.Auth.SingleAsync(u => u.UserUID == primaryId).ConfigureAwait(false);
        }
        if (user.MarkForBan || user.IsBanned)
        {
            await RespondAsync("Error: user is already banned", ephemeral: true).ConfigureAwait(false);
            return;
        }
        user.MarkForBan = true;
        var lodeStoneAuth = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == user.UserUID || u.User.UID == user.PrimaryUserUID).ConfigureAwait(false);
        if (lodeStoneAuth != null)
        {
            dbContext.BannedRegistrations.Add(new BannedRegistrations
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.HashedLodestoneId,
            });
            dbContext.BannedRegistrations.Add(new BannedRegistrations
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.DiscordId.ToString(),
            });
        }

        // Add all CharaIds under the primary UID
        if (user.CharaIds is not null)
        {
            foreach (var id in user.CharaIds)
            {
                if (!dbContext.BannedUsers.Any(c => c.CharacterIdentification == id))
                {
                    dbContext.BannedUsers.Add(new Banned
                    {
                        CharacterIdentification = id,
                        Reason = "Character ban (" + uid + ")",
                    });
                }
            }
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var text = $"User `{uid}` has been added to the ban list";
        if (!string.IsNullOrEmpty(reason)) text += $", reason: `{reason}`";
        await RespondAsync(text, ephemeral: false).ConfigureAwait(false);
        _logger.LogInformation($"Admin {Context.Interaction.User.Username} banned {uid}");
    }

    [SlashCommand("unbanuser", "Unban user")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task UnBanUser([Summary("uid", "User UID")] string uid)
    {

        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

        var user = await dbContext.Auth.SingleAsync(u => u.UserUID == uid).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(user.PrimaryUserUID))
        {
            var primaryId = user.PrimaryUserUID;
            user = await dbContext.Auth.SingleAsync(u => u.UserUID == primaryId).ConfigureAwait(false);
        }
        if (!user.MarkForBan && !user.IsBanned)
        {
            await RespondAsync("Error: user is not banned", ephemeral: true).ConfigureAwait(false);
            return;
        }
        user.MarkForBan = false;
        user.IsBanned = false;
        var lodeStoneAuth = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == user.UserUID || u.User.UID == user.PrimaryUserUID).ConfigureAwait(false);
        if (lodeStoneAuth != null)
        {
            dbContext.BannedRegistrations.Remove(new BannedRegistrations
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.HashedLodestoneId,
            });
            dbContext.BannedRegistrations.Remove(new BannedRegistrations
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.DiscordId.ToString(),
            });
        }

        // Remove all CharaIds under the primary UID
        if (user.CharaIds is not null)
        {
            foreach (var id in user.CharaIds)
            {
                var bannedusers = dbContext.BannedUsers.FirstOrDefault(c => c.CharacterIdentification == id);
                if (bannedusers != null)
                {
                    dbContext.BannedUsers.Remove(bannedusers);
                }
            }
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var text = $"User `{uid}` has been unbanned";
        await RespondAsync(text, ephemeral: true).ConfigureAwait(false);
        _logger.LogInformation($"Admin {Context.Interaction.User.Username} unbanned {uid}");

    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("warnuser", "Admins only: warn a user")]
    public async Task WarnUser(
        [Summary("reason", "Reason for warning")] string reason,
        [Summary("discord_user", "Discord user")] IUser? desiredDCAccount = null,
        [Summary("uid", "User UID")] string? desiredUid = null
        )
    {
        try
        {
            using var scope = _services.CreateScope();
            using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

            var roleId = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.WarningRole), 1329441701487575070);

            if (await Context.Guild.GetRoleAsync(roleId.Value).ConfigureAwait(false) is null)
            {
                await RespondAsync($"Role <{roleId}> not found; please check and try again.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            if ((string.IsNullOrEmpty(desiredUid) && desiredDCAccount is null) || (!string.IsNullOrEmpty(desiredUid) && desiredDCAccount is not null))
            {
                await RespondAsync("Exactly one of UID or Discord account must be provided; please check and try again.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            ulong dcid = 0;

            if (desiredUid != null)
            {
                var auth = await dbContext.Auth.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.UserUID == desiredUid || a.PrimaryUserUID == desiredUid)
                    .ConfigureAwait(false);
                if (auth is null)
                {
                    await RespondAsync("UID not found; please check and try again.", ephemeral: true).ConfigureAwait(false);
                    return;
                }

                var lodeStoneAuth = await dbContext.LodeStoneAuth.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.User.UID == auth.UserUID).ConfigureAwait(false);
                if (lodeStoneAuth is null)
                {
                    await RespondAsync("No Discord account found for the given UID; please check and try again.", ephemeral: true).ConfigureAwait(false);
                    return;
                }

                dcid = lodeStoneAuth.DiscordId;
            }

            if (desiredDCAccount != null)
            {
                var lodeStoneAuth = await dbContext.LodeStoneAuth.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.DiscordId == desiredDCAccount.Id).ConfigureAwait(false);
                if (lodeStoneAuth is null)
                {
                    await RespondAsync("No UID found for the given Discord account; please check and try again.", ephemeral: true).ConfigureAwait(false);
                    return;
                }

                dcid = lodeStoneAuth.DiscordId;
            }

            var discordUser = await Context.Guild.GetUserAsync(dcid).ConfigureAwait(false);
            if (discordUser is null)
            {
                await RespondAsync($"Could not find <@{dcid}>; please check and try again.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            var warning = await dbContext.Warnings.FirstOrDefaultAsync(x => x.DiscordId == dcid).ConfigureAwait(false);

            var reportChannelId = _mareServicesConfiguration.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForReports));
            var restChannel = await Context.Guild.GetTextChannelAsync(reportChannelId.Value).ConfigureAwait(false);

            if (warning is null || warning.Time.AddMonths(6) < DateTime.UtcNow) // first time
            {
                await discordUser.AddRoleAsync(roleId.Value).ConfigureAwait(false);
                if (warning is null)
                {
                    var newWarning = new Warning
                    {
                        DiscordId = dcid,
                        Time = DateTime.UtcNow,
                        Reason = reason,
                    };
                    await dbContext.AddAsync(newWarning).ConfigureAwait(false);
                }
                else
                {
                    warning.Time = DateTime.UtcNow;
                    warning.Reason = reason;
                }
                await dbContext.SaveChangesAsync().ConfigureAwait(false);

                await restChannel.SendMessageAsync($"Warned user <@{dcid}>, reason: `{reason}`").ConfigureAwait(false);
                await RespondAsync($"Warned user <@{dcid}>, reason: `{reason}`", ephemeral: true).ConfigureAwait(false);
            }
            else // second time -> escalate
            {
                warning.Time = DateTime.UtcNow;
                warning.Reason = reason;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);

                var uidToFind = await dbContext.LodeStoneAuth.AsNoTracking().FirstOrDefaultAsync(x => x.DiscordId == dcid)
                    .ConfigureAwait(false);
                var userToBanUid = uidToFind?.User?.UID;
                if (string.IsNullOrEmpty(userToBanUid))
                {
                    await RespondAsync("Could not get the user's in-game UID to ban; please check data.", ephemeral: true).ConfigureAwait(false);
                    return;
                }
                await BanUser(userToBanUid, reason).ConfigureAwait(false);

                await restChannel.SendMessageAsync($"Warned user <@{dcid}>, reason: `{reason}`; escalated to ban.").ConfigureAwait(false);
                await RespondAsync($"Warned user <@{dcid}>, reason: `{reason}`; escalated to ban.", ephemeral: true).ConfigureAwait(false);
            }

        }
        catch (Exception e)
        {
            await RespondAsync(e.Message, ephemeral: true).ConfigureAwait(false);
            _logger.LogError(e.ToString());
        }

    }

    [SlashCommand("forbiddenfiles", "Manage forbidden files")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task ForbiddenFiles(Addremove arg, string hash, string? reason = null)
    {
        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

        var data = dbContext.ForbiddenUploadEntries.FirstOrDefault(x => x.Hash == hash);
        if (arg == Addremove.Add)
        {
            if (data != null)
            {
                await ReplyAsync($"Entry with hash {hash} already exists" + Environment.NewLine + "Forbidden by: " + data.ForbiddenBy + Environment.NewLine + "Time: " + Encoding.UTF8.GetString(data.Timestamp)).ConfigureAwait(false);
                return;
            }
            data = new ForbiddenUploadEntry() { Hash = hash, ForbiddenBy = reason, Timestamp = Encoding.UTF8.GetBytes(DateTime.Now.ToString(CultureInfo.CurrentCulture)) };
            dbContext.ForbiddenUploadEntries.Add(data);
            await ReplyAsync($"Added entry: {hash} to the database").ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        else
        {
            if (data == null)
            {
                await ReplyAsync($"Entry with hash {hash} does not exist").ConfigureAwait(false);
                return;
            }
            dbContext.ForbiddenUploadEntries.Remove(data);
            await ReplyAsync($"Removed entry: {hash} from the database").ConfigureAwait(false);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public enum Addremove
    {
        Add,
        Remove
    }
}
