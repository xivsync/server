using System.Text;
using System.Net.Http;
using System.Net.Http.Json;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using MareSynchronos.API.Data.Enum;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IConfigurationService<ServicesConfiguration> _configurationService;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<DiscordBot> _logger;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly IServiceProvider _services;
    private readonly ServerTokenGenerator _serverTokenGenerator;

    private InteractionService _interactionModule;
    private CancellationTokenSource? _processReportQueueCts;
    private CancellationTokenSource? _clientConnectedCts;

    public DiscordBot(
        DiscordBotServices botServices,
        IServiceProvider services,
        IConfigurationService<ServicesConfiguration> configuration,
        IDbContextFactory<MareDbContext> dbContextFactory,
        ILogger<DiscordBot> logger,
        IConnectionMultiplexer connectionMultiplexer,
        ServerTokenGenerator serverTokenGenerator
    )
    {
        _botServices = botServices;
        _services = services;
        _configurationService = configuration;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
        _serverTokenGenerator = serverTokenGenerator;

        _discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry,
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        });

        _discordClient.Log += Log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartAsync invoked");

        var token = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty);
        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogInformation("Starting DiscordBot");
            _logger.LogInformation("Using Configuration: " + _configurationService.ToString());

            _interactionModule?.Dispose();
            _interactionModule = new InteractionService(_discordClient);
            _interactionModule.Log += Log;
            await _interactionModule.AddModuleAsync(typeof(MareModule), _services).ConfigureAwait(false);
            await _interactionModule.AddModuleAsync(typeof(MareWizardModule), _services).ConfigureAwait(false);

            // Attach Ready *before* connecting, just in case
            _discordClient.Ready += DiscordClient_Ready;

            _logger.LogInformation("Logging in...");
            await _discordClient.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            _logger.LogInformation("LoginAsync completed");

            _logger.LogInformation("Starting gateway connection...");
            await _discordClient.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("StartAsync on DiscordSocketClient completed. ConnectionState={state}", _discordClient.ConnectionState);

            _discordClient.ButtonExecuted += ButtonExecutedHandler;
            _discordClient.InteractionCreated += async (x) =>
            {
                var ctx = new SocketInteractionContext(_discordClient, x);
                await _interactionModule.ExecuteCommandAsync(ctx, _services).ConfigureAwait(false);
            };
            _discordClient.UserJoined += OnUserJoined;

            // Watchdog: warn if Ready hasn't fired soon
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                if (_discordClient.ConnectionState != ConnectionState.Connected)
                {
                    _logger.LogWarning("Discord Ready not observed within 30s. Current state: {state}. Check token, intents, and network reachability.",
                        _discordClient.ConnectionState);
                }
            }, cancellationToken);

            await _botServices.Start().ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Discord bot token missing; bot not started.");
        }
    }

    private async Task OnUserJoined(SocketGuildUser arg)
    {
        try
        {
            using MareDbContext dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var alreadyRegistered = await dbContext.LodeStoneAuth.AnyAsync(u => u.DiscordId == arg.Id).ConfigureAwait(false);
            if (alreadyRegistered)
            {
                await _botServices.AddRegisteredRoleAsync(arg).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set user role on join");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty)))
        {
            _discordClient.ButtonExecuted -= ButtonExecutedHandler;

            await _botServices.Stop().ConfigureAwait(false);
            _processReportQueueCts?.Cancel();
            _clientConnectedCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
            _interactionModule?.Dispose();
        }
    }

    private async Task DiscordClient_Ready()
    {
        try
        {
            _logger.LogInformation("DiscordClient_Ready fired");

            var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
            _logger.LogInformation("Registering interaction commands to guild {guildId}", guild.Id);
            await _interactionModule.RegisterCommandsToGuildAsync(guild.Id, true).ConfigureAwait(false);

            _clientConnectedCts?.Cancel();
            _clientConnectedCts?.Dispose();
            _clientConnectedCts = new();

            _logger.LogInformation("Spawning background loops after Ready");
            _ = UpdateStatusAsync(_clientConnectedCts.Token);

            await CreateOrUpdateModal(guild).ConfigureAwait(false);
            _botServices.UpdateGuild(guild);
            await _botServices.LogToChannel("Bot startup complete.").ConfigureAwait(false);

            _ = UpdateVanityRoles(guild, _clientConnectedCts.Token);
            _ = RemoveUsersNotInVanityRole(_clientConnectedCts.Token);
            _ = RemoveUnregisteredUsers(_clientConnectedCts.Token);
            _ = ProcessReportsQueue();
            _ = CheckSupporters(_clientConnectedCts.Token);

            _logger.LogInformation("DiscordClient_Ready completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in DiscordClient_Ready");
        }
    }

    private async Task CheckSupporters(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating Supporter Roles");
                ulong? supporter = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.Supporter), new ulong?());
                if (supporter == null) return;
                var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id).ConfigureAwait(false);
                using var db = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);

                var users = db.Supports.Where(x => x.ExpiresAt < DateTime.UtcNow).Select(x => x.DiscordId).ToList();

                foreach (var user in users)
                {
                    var discordUser = await restGuild.GetUserAsync(user).ConfigureAwait(false);
                    if (discordUser != null)
                    {
                        if (discordUser.RoleIds.Contains(supporter.Value))
                        {
                            await _discordClient.Rest.RemoveRoleAsync(guild.Id, discordUser.Id, supporter.Value).ConfigureAwait(false);
                            _logger.LogInformation($"Supporter Role Removed from {discordUser.Id}");
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromHours(12), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during CheckSupporters");
            }
        }
    }

    private async Task UpdateVanityRoles(RestGuild guild, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating Vanity Roles");
                Dictionary<ulong, string> vanityRoles = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                if (vanityRoles.Keys.Count != _botServices.VanityRoles.Count)
                {
                    _botServices.VanityRoles.Clear();
                    foreach (var role in vanityRoles)
                    {
                        _logger.LogInformation("Adding Role: {id} => {desc}", role.Key, role.Value);

                        var restrole = guild.GetRole(role.Key);
                        if (restrole != null)
                            _botServices.VanityRoles[restrole] = role.Value;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during UpdateVanityRoles");
            }
        }
    }

    private async Task CreateOrUpdateModal(RestGuild guild)
    {
        _logger.LogInformation("Creating Wizard: Getting Channel");

        var discordChannelForCommands = _configurationService.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForCommands));
        if (discordChannelForCommands == null)
        {
            _logger.LogWarning("Creating Wizard: No channel configured");
            return;
        }

        IUserMessage? message = null;
        var socketchannel = await _discordClient.GetChannelAsync(discordChannelForCommands.Value).ConfigureAwait(false) as SocketTextChannel;
        var pinnedMessages = await socketchannel.GetPinnedMessagesAsync().ConfigureAwait(false);
        foreach (var msg in pinnedMessages)
        {
            _logger.LogInformation("Creating Wizard: Checking message id {id}, author is: {author}, hasEmbeds: {embeds}", msg.Id, msg.Author.Id, msg.Embeds.Any());
            if (msg.Author.Id == _discordClient.CurrentUser.Id
                && msg.Embeds.Any())
            {
                message = await socketchannel.GetMessageAsync(msg.Id).ConfigureAwait(false) as IUserMessage;
                break;
            }
        }

        _logger.LogInformation("Creating Wizard: Found message id: {id}", message?.Id ?? 0);

        await GenerateOrUpdateWizardMessage(socketchannel, message).ConfigureAwait(false);
    }

    private async Task GenerateOrUpdateWizardMessage(SocketTextChannel channel, IUserMessage? prevMessage)
    {
        EmbedBuilder eb = new EmbedBuilder();
        eb.WithTitle("XIVSync Services Interaction Service");
        eb.WithDescription("Press \"Start\" to interact with this bot!" + Environment.NewLine + Environment.NewLine
            + "You can handle all of your Mare account needs in this server through the easy to use interactive bot prompt. Just follow the instructions!");
        eb.WithThumbnailUrl("https://raw.githubusercontent.com/xivsync/repo/main/XIVSync/images/icon.png");
        var cb = new ComponentBuilder();
        cb.WithButton("Start", style: ButtonStyle.Primary, customId: "wizard-captcha:true", emote: Emoji.Parse("➡️"));
        if (prevMessage == null)
        {
            var msg = await channel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
            try
            {
                await msg.PinAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // swallow
            }
        }
        else
        {
            await prevMessage.ModifyAsync(p =>
            {
                p.Embed = eb.Build();
                p.Components = cb.Build();
            }).ConfigureAwait(false);
        }
    }

    private Task Log(LogMessage msg)
    {
        switch (msg.Severity)
        {
            case LogSeverity.Critical:
            case LogSeverity.Error:
                _logger.LogError(msg.Exception, msg.Message); break;
            case LogSeverity.Warning:
                _logger.LogWarning(msg.Exception, msg.Message); break;
            default:
                _logger.LogInformation(msg.Message); break;
        }

        return Task.CompletedTask;
    }

    private async Task RemoveUnregisteredUsers(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProcessUserRoles(guild, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // do nothing
            }
            catch (Exception ex)
            {
                await _botServices.LogToChannel($"Error during user procesing: {ex.Message}").ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromDays(1)).ConfigureAwait(false);
        }
    }

    private async Task ProcessUserRoles(RestGuild guild, CancellationToken token)
    {
        using MareDbContext dbContext = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var roleId = _configurationService.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleRegistered), 0);
        var kickUnregistered = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.KickNonRegisteredUsers), false);
        if (roleId == null) return;

        var registrationRole = guild.Roles.FirstOrDefault(f => f.Id == roleId.Value);
        var registeredUsers = new HashSet<ulong>(await dbContext.LodeStoneAuth.AsNoTracking().Select(c => c.DiscordId).ToListAsync().ConfigureAwait(false));

        var executionStartTime = DateTimeOffset.UtcNow;

        int processedUsers = 0;
        int addedRoles = 0;
        int kickedUsers = 0;
        int totalRoles = 0;
        int toRemoveUsers = 0;
        int freshUsers = 0;

        await _botServices.LogToChannel($"Starting to process registered users: Adding Role {registrationRole.Name}. Kick Stale Unregistered: {kickUnregistered}.").ConfigureAwait(false);

        await foreach (var userList in guild.GetUsersAsync(new RequestOptions { CancelToken = token }).ConfigureAwait(false))
        {
            _logger.LogInformation("Processing chunk of {count} users, total processed: {proc}, total roles: {total}, roles added: {added}, users kicked: {kicked}, users plan to kick: {planToKick}, fresh user: {fresh}",
                userList.Count, processedUsers, totalRoles + addedRoles, addedRoles, kickedUsers, toRemoveUsers, freshUsers);
            foreach (var user in userList)
            {
                if (user.IsBot) continue;

                if (registeredUsers.Contains(user.Id))
                {
                    bool roleAdded = await _botServices.AddRegisteredRoleAsync(user, registrationRole).ConfigureAwait(false);
                    if (roleAdded) addedRoles++;
                    else totalRoles++;
                }
                else
                {
                    if ((executionStartTime - user.JoinedAt.Value).TotalDays > 7)
                    {
                        if (kickUnregistered)
                        {
                            await _botServices.KickUserAsync(user).ConfigureAwait(false);
                            kickedUsers++;
                        }
                        else
                        {
                            toRemoveUsers++;
                        }
                    }
                    else
                    {
                        freshUsers++;
                    }
                }

                token.ThrowIfCancellationRequested();
                processedUsers++;
            }
        }

        await _botServices.LogToChannel($"Processing registered users finished. Processed {processedUsers} users, added {addedRoles} roles and kicked {kickedUsers} users").ConfigureAwait(false);
    }

    private async Task RemoveUsersNotInVanityRole(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Cleaning up Vanity UIDs");
                await _botServices.LogToChannel("Cleaning up Vanity UIDs").ConfigureAwait(false);
                _logger.LogInformation("Getting rest guild {guildName}", guild.Name);
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id).ConfigureAwait(false);

                Dictionary<ulong, string> allowedRoleIds = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                _logger.LogInformation($"Allowed role ids: {string.Join(", ", allowedRoleIds)}");

                if (allowedRoleIds.Any())
                {
                    using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

                    var aliasedUsers = await db.LodeStoneAuth.Include("User")
                        .Where(c => c.User != null && !string.IsNullOrEmpty(c.User.Alias)).ToListAsync().ConfigureAwait(false);
                    var aliasedGroups = await db.Groups.Include(u => u.Owner)
                        .Where(c => !string.IsNullOrEmpty(c.Alias)).ToListAsync().ConfigureAwait(false);

                    foreach (var lodestoneAuth in aliasedUsers)
                    {
                        await CheckVanityForUser(restGuild, allowedRoleIds, db, lodestoneAuth, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }

                    foreach (var group in aliasedGroups)
                    {
                        await CheckVanityForGroup(restGuild, allowedRoleIds, db, group, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogInformation("No roles for command defined, no cleanup performed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Something failed during checking vanity user uids");
            }

            _logger.LogInformation("Vanity UID cleanup complete");
            await Task.Delay(TimeSpan.FromHours(12), token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForGroup(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, MareDbContext db, Group group, CancellationToken token)
    {
        var groupPrimaryUser = group.OwnerUID;
        var groupOwner = await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.UserUID == group.OwnerUID).ConfigureAwait(false);
        if (groupOwner != null && !string.IsNullOrEmpty(groupOwner.PrimaryUserUID))
        {
            groupPrimaryUser = groupOwner.PrimaryUserUID;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(f => f.User.UID == groupPrimaryUser).ConfigureAwait(false);
        RestGuildUser discordUser = null;
        if (lodestoneUser != null)
        {
            discordUser = await restGuild.GetUserAsync(lodestoneUser.DiscordId).ConfigureAwait(false);
        }

        _logger.LogInformation($"Checking Group: {group.GID} [{group.Alias}], owned by {group.OwnerUID} ({groupPrimaryUser}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (lodestoneUser == null || discordUser == null || !discordUser.RoleIds.Any(allowedRoleIds.Keys.Contains))
        {
            await _botServices.LogToChannel($"VANITY GID REMOVAL: <@{lodestoneUser?.DiscordId ?? 0}> ({lodestoneUser?.User?.UID}) - GID: {group.GID}, Vanity: {group.Alias}").ConfigureAwait(false);

            _logger.LogInformation($"User {lodestoneUser?.User?.UID ?? "unknown"} not in allowed roles, deleting group alias for {group.GID}");
            group.Alias = null;
            db.Update(group);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForUser(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, MareDbContext db, LodeStoneAuth lodestoneAuth, CancellationToken token)
    {
        var discordUser = await restGuild.GetUserAsync(lodestoneAuth.DiscordId).ConfigureAwait(false);
        _logger.LogInformation($"Checking User: {lodestoneAuth.DiscordId}, {lodestoneAuth.User.UID} ({lodestoneAuth.User.Alias}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Keys.Contains(u)))
        {
            _logger.LogInformation($"User {lodestoneAuth.User.UID} not in allowed roles, deleting alias");
            await _botServices.LogToChannel($"VANITY UID REMOVAL: <@{lodestoneAuth.DiscordId}> - UID: {lodestoneAuth.User.UID}, Vanity: {lodestoneAuth.User.Alias}").ConfigureAwait(false);
            lodestoneAuth.User.Alias = null;
            var secondaryUsers = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == lodestoneAuth.User.UID).ToListAsync().ConfigureAwait(false);
            foreach (var secondaryUser in secondaryUsers)
            {
                _logger.LogInformation($"Secondary User {secondaryUser.User.UID} not in allowed roles, deleting alias");

                secondaryUser.User.Alias = null;
                db.Update(secondaryUser.User);
            }
            db.Update(lodestoneAuth.User);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var endPoint = _connectionMultiplexer.GetEndPoints().First();
            var onlineUsers = await _connectionMultiplexer.GetServer(endPoint).KeysAsync(pattern: "UID:*").CountAsync().ConfigureAwait(false);

            _logger.LogInformation("Users online: " + onlineUsers);
            await _discordClient.SetActivityAsync(new Game("XIVSync for " + onlineUsers + " Users")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    // ===== Report queue & moderation =====

    private async Task ButtonExecutedHandler(SocketMessageComponent arg)
    {
        var id = arg.Data.CustomId;
        if (!id.StartsWith("mare-report-button", StringComparison.Ordinal)) return;

        var userId = arg.User.Id;
        using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var user = await dbContext.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == userId).ConfigureAwait(false);

        if (user == null || (!user.User.IsModerator && !user.User.IsAdmin))
        {
            EmbedBuilder eb = new();
            eb.WithTitle($"Insufficient Permission");
            eb.WithDescription($"<@{userId}>: you are not allowed to handle reports");
            await arg.RespondAsync(embed: eb.Build(), ephemeral: true).ConfigureAwait(false);
            return;
        }

        id = id.Remove(0, "mare-report-button-".Length);
        var split = id.Split('-', StringSplitOptions.RemoveEmptyEntries);

        var profile = await dbContext.UserProfileData.SingleAsync(u => u.UserUID == split[1]).ConfigureAwait(false);

        var embed = arg.Message.Embeds.First();
        var builder = embed.ToEmbedBuilder();
        var otherPairs = await dbContext.ClientPairs.Where(p => p.UserUID == split[1]).Select(p => p.OtherUserUID).ToListAsync().ConfigureAwait(false);

        switch (split[0])
        {
            case "dismiss":
                builder.AddField("Decision", $"Report was dismissed by <@{userId}>");
                builder.WithColor(Color.Green);
                profile.FlaggedForReport = false;
                await SendMessageToClients("A report against you has been dismissed.", MessageSeverity.Warning, split[1]).ConfigureAwait(false);
                break;

            case "banreporting":
                builder.AddField("Decision", $"Report was dismissed by <@{userId}>, reporter banned");
                builder.WithColor(Color.DarkGreen);
                profile.FlaggedForReport = false;
                var reportingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[2]).ConfigureAwait(false);
                reportingUser.MarkForBan = true;
                var regReporting = await dbContext.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.User.UID == reportingUser.UserUID || u.User.UID == reportingUser.PrimaryUserUID).ConfigureAwait(false);
                BanAuth(dbContext, regReporting);
                await SendMessageToClients("A report against you has been dismissed.", MessageSeverity.Warning, split[1]).ConfigureAwait(false);
                break;

            case "banuser":
                builder.AddField("Decision", $"User banned by <@{userId}>");
                builder.WithColor(Color.DarkRed);
                var offendingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[1]).ConfigureAwait(false);
                offendingUser.MarkForBan = true;
                profile.Base64ProfileImage = null;
                profile.UserDescription = null;
                profile.ProfileDisabled = true;
                var reg = await dbContext.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.User.UID == offendingUser.UserUID || u.User.UID == offendingUser.PrimaryUserUID).ConfigureAwait(false);
                BanAuth(dbContext, reg);
                await SendMessageToClients("Due to a report against you, your account will be banned. If you believe this is in error, please contact moderators.", MessageSeverity.Warning, split[1]).ConfigureAwait(false);
                break;

            case "banboth":
                builder.AddField("Decision", $"Both users banned by <@{userId}>");
                builder.WithColor(Color.DarkRed);
                offendingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[1]).ConfigureAwait(false);
                offendingUser.MarkForBan = true;
                profile.Base64ProfileImage = null;
                profile.UserDescription = null;
                profile.ProfileDisabled = true;
                reg = await dbContext.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.User.UID == offendingUser.UserUID || u.User.UID == offendingUser.PrimaryUserUID).ConfigureAwait(false);
                BanAuth(dbContext, reg);

                profile.FlaggedForReport = false;
                reportingUser = await dbContext.Auth.SingleAsync(u => u.UserUID == split[2]).ConfigureAwait(false);
                reportingUser.MarkForBan = true;
                regReporting = await dbContext.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.User.UID == reportingUser.UserUID || u.User.UID == reportingUser.PrimaryUserUID).ConfigureAwait(false);
                BanAuth(dbContext, regReporting);
                break;
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        await arg.Message.ModifyAsync(msg =>
        {
            msg.Content = arg.Message.Content;
            msg.Components = null;
            msg.Embed = new Optional<Embed>(builder.Build());
        }).ConfigureAwait(false);
    }

    private void BanAuth(MareDbContext dbContext, LodeStoneAuth lodeStoneAuth)
    {
        if (!dbContext.BannedRegistrations.Any(x => x.DiscordIdOrLodestoneAuth == lodeStoneAuth.HashedLodestoneId))
        {
            dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.HashedLodestoneId
            });
        }
        if (!dbContext.BannedRegistrations.Any(x => x.DiscordIdOrLodestoneAuth == lodeStoneAuth.DiscordId.ToString()))
        {
            dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
            {
                DiscordIdOrLodestoneAuth = lodeStoneAuth.DiscordId.ToString()
            });
        }
    }

    private async Task ProcessReportsQueue()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync()).First();

        _processReportQueueCts?.Cancel();
        _processReportQueueCts?.Dispose();
        _processReportQueueCts = new();
        var token = _processReportQueueCts.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            if (_discordClient.ConnectionState != ConnectionState.Connected) continue;
            var reportChannelId = _configurationService.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForReports));
            if (reportChannelId == null) continue;

            try
            {
                using (var scope = _services.CreateScope())
                {
                    _logger.LogInformation("Checking for Profile Reports");
                    var dbContext = scope.ServiceProvider.GetRequiredService<MareDbContext>();
                    if (!dbContext.UserProfileReports.Any())
                    {
                        continue;
                    }

                    var reports = await dbContext.UserProfileReports.ToListAsync().ConfigureAwait(false);
                    var restChannel = await guild.GetTextChannelAsync(reportChannelId.Value).ConfigureAwait(false);

                    foreach (var report in reports)
                    {
                        var reportedUser = await dbContext.Users.SingleAsync(u => u.UID == report.ReportedUserUID).ConfigureAwait(false);
                        var reportedUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == report.ReportedUserUID).ConfigureAwait(false);
                        if (reportedUserLodestone == null)
                        {
                            var reportedPrimary = await dbContext.Auth.SingleOrDefaultAsync(u => u.UserUID == report.ReportedUserUID && !string.IsNullOrEmpty(u.PrimaryUserUID)).ConfigureAwait(false);
                            reportedUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == reportedPrimary.PrimaryUserUID).ConfigureAwait(false);
                        }
                        var reportingUser = await dbContext.Users.SingleAsync(u => u.UID == report.ReportingUserUID).ConfigureAwait(false);
                        var reportingUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == report.ReportingUserUID).ConfigureAwait(false);
                        if (reportingUserLodestone == null)
                        {
                            var reportingPrimary = await dbContext.Auth.SingleOrDefaultAsync(u => u.UserUID == report.ReportingUserUID && !string.IsNullOrEmpty(u.PrimaryUserUID)).ConfigureAwait(false);
                            reportingUserLodestone = await dbContext.LodeStoneAuth.SingleOrDefaultAsync(u => u.User.UID == reportingPrimary.PrimaryUserUID).ConfigureAwait(false);
                        }
                        var reportedUserProfile = await dbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == report.ReportedUserUID).ConfigureAwait(false);
                        if (reportedUserProfile is null)
                        {
                            reportedUserProfile = new UserProfileData() { UserUID = reportedUser.UID, Base64ProfileImage = null, UserDescription = null, IsNSFW = false };
                            dbContext.UserProfileData.Add(reportedUserProfile);
                            await dbContext.SaveChangesAsync().ConfigureAwait(false);
                        }

                        EmbedBuilder eb = new();
                        eb.WithTitle("Mare Report");

                        StringBuilder reportedUserSb = new();
                        StringBuilder reportingUserSb = new();
                        reportedUserSb.Append(reportedUser.UID);
                        reportingUserSb.Append(reportingUser.UID);
                        if (reportedUserLodestone != null)
                        {
                            reportedUserSb.AppendLine($" (<@{reportedUserLodestone.DiscordId}>)");
                        }
                        if (reportingUserLodestone != null)
                        {
                            reportingUserSb.AppendLine($" (<@{reportingUserLodestone.DiscordId}>)");
                        }
                        eb.AddField("Reported User", reportedUserSb.ToString());
                        eb.AddField("Reporting User", reportingUserSb.ToString());
                        eb.AddField("Reported At", $"<t:{new DateTimeOffset(report.ReportDate).ToUnixTimeSeconds()}:f>");
                        eb.AddField("Reason", string.IsNullOrWhiteSpace(report.ReportReason) ? "-" : report.ReportReason);
                        eb.AddField("Profile Text", string.IsNullOrWhiteSpace(reportedUserProfile.UserDescription) ? "-" : reportedUserProfile.UserDescription);
                        eb.AddField("Profile NSFW", reportedUserProfile.IsNSFW ? "Yes" : "No");

                        var cb = new ComponentBuilder();
                        cb.WithButton("Dismiss", customId: $"mare-report-button-dismiss-{reportedUser.UID}", style: ButtonStyle.Primary);
                        cb.WithButton("Ban user", customId: $"mare-report-button-banuser-{reportedUser.UID}", style: ButtonStyle.Danger);
                        cb.WithButton("Dismiss & Ban reporter", customId: $"mare-report-button-banreporting-{reportedUser.UID}-{reportingUser.UID}", style: ButtonStyle.Danger);
                        cb.WithButton("Ban both", customId: $"mare-report-button-banboth-{reportedUser.UID}-{reportingUser.UID}", style: ButtonStyle.Danger);

                        RestUserMessage msg = null;
                        if (!string.IsNullOrEmpty(reportedUserProfile.Base64ProfileImage))
                        {
                            var fileName = reportedUser.UID + "_profile_" + Guid.NewGuid().ToString("N") + ".png";
                            eb.WithImageUrl($"attachment://{fileName}");
                            using MemoryStream ms = new(Convert.FromBase64String(reportedUserProfile.Base64ProfileImage));
                            msg = await restChannel.SendFileAsync(ms, fileName, "User Report", embed: eb.Build(), components: cb.Build(), isSpoiler: true).ConfigureAwait(false);
                        }
                        else
                        {
                            msg = await restChannel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
                        }

                        var thread = await restChannel.CreateThreadAsync(
                            type: ThreadType.PrivateThread,
                            name: $"Report: {reportingUser.UID} -> {reportedUser.UID}",
                            invitable: true,
                            autoArchiveDuration: ThreadArchiveDuration.ThreeDays).ConfigureAwait(false);

                        dbContext.Remove(report);

                        await thread.SendMessageAsync($"Both parties <@{reportingUserLodestone?.DiscordId}> <@{reportedUserLodestone?.DiscordId}> please provide evidence within 72h for <@&1301329024680857692> to review.").ConfigureAwait(false);
                    }

                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process reports");
            }
        }
    }

    public async Task SendMessageToClients(string message, MessageSeverity messageType = MessageSeverity.Information, string? uid = null)
    {
        try
        {
            using HttpClient c = new HttpClient();
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _serverTokenGenerator.Token);
            await c.PostAsJsonAsync(new Uri(_configurationService.GetValue<Uri>(nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage"),
                new ClientMessage(messageType, message, uid ?? string.Empty)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to client(s): ");
        }
    }
}
