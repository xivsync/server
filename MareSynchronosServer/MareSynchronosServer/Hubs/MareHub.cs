using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Dto;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Services;
using MareSynchronosServer.Utils;
using MareSynchronosShared;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;
using System.Collections.Concurrent;
using MareSynchronos.API.Dto.User;

namespace MareSynchronosServer.Hubs;

[Authorize(Policy = "Authenticated")]
public partial class MareHub : Hub<IMareHub>, IMareHub
{
    private static readonly ConcurrentDictionary<string, string> _userConnections = new(StringComparer.Ordinal);
    private readonly MareMetrics _mareMetrics;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly MareHubLogger _logger;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;
    private readonly IRedisDatabase _redis;
    private readonly OnlineSyncedPairCacheService _onlineSyncedPairCacheService;
    private readonly MareCensus _mareCensus;
    private readonly Uri _fileServerAddress;
    private readonly Version _expectedClientVersion;
    private readonly Lazy<MareDbContext> _dbContextLazy;
    private MareDbContext DbContext => _dbContextLazy.Value;

    public MareHub(MareMetrics mareMetrics,
        IDbContextFactory<MareDbContext> mareDbContextFactory, ILogger<MareHub> logger, SystemInfoService systemInfoService,
        IConfigurationService<ServerConfiguration> configuration, IHttpContextAccessor contextAccessor,
        IRedisDatabase redisDb, OnlineSyncedPairCacheService onlineSyncedPairCacheService, MareCensus mareCensus)
    {
        _mareMetrics = mareMetrics;
        _systemInfoService = systemInfoService;
        _shardName = configuration.GetValue<string>(nameof(ServerConfiguration.ShardName));
        _maxExistingGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
        _maxJoinedGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxJoinedGroupsByUser), 6);
        _maxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 100);
        _fileServerAddress = configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        _expectedClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.ExpectedClientVersion), new Version(0, 0, 0));
        _contextAccessor = contextAccessor;
        _redis = redisDb;
        _onlineSyncedPairCacheService = onlineSyncedPairCacheService;
        _mareCensus = mareCensus;
        _logger = new MareHubLogger(this, logger);
        _dbContextLazy = new Lazy<MareDbContext>(() => mareDbContextFactory.CreateDbContext());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_dbContextLazy.IsValueCreated) DbContext.Dispose();
        }

        base.Dispose(disposing);
    }

    [Authorize(Policy = "Identified")]
    public async Task<ConnectionDto> GetConnectionDto()
    {
        _logger.LogCallInfo();

        _mareMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        await Clients.Caller.Client_UpdateSystemInfo(_systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var dbUser = await DbContext.Users.SingleAsync(f => f.UID == UserUID).ConfigureAwait(false);
        dbUser.LastLoggedIn = DateTime.UtcNow;

        await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, "Welcome to Mare Synchronos \"" + _shardName + "\", Current Online Users: " + _systemInfoService.SystemInfoDto.OnlineUsers).ConfigureAwait(false);

        var defaultPermissions = await DbContext.UserDefaultPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == UserUID).ConfigureAwait(false);
        if (defaultPermissions == null)
        {
            defaultPermissions = new UserDefaultPreferredPermission()
            {
                UserUID = UserUID,
            };

            DbContext.UserDefaultPreferredPermissions.Add(defaultPermissions);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return new ConnectionDto(new UserData(dbUser.UID, string.IsNullOrWhiteSpace(dbUser.Alias) ? null : dbUser.Alias))
        {
            CurrentClientVersion = _expectedClientVersion,
            ServerVersion = IMareHub.ApiVersion,
            IsAdmin = dbUser.IsAdmin,
            IsModerator = dbUser.IsModerator,
            ServerInfo = new ServerInfo()
            {
                MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                ShardName = _shardName,
                MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                MaxGroupUserCount = _maxGroupUserCount,
                FileServerAddress = _fileServerAddress,
            },
            DefaultPreferredPermissions = new DefaultPermissionsDto()
            {
                DisableGroupAnimations = defaultPermissions.DisableGroupAnimations,
                DisableGroupSounds = defaultPermissions.DisableGroupSounds,
                DisableGroupVFX = defaultPermissions.DisableGroupVFX,
                DisableIndividualAnimations = defaultPermissions.DisableIndividualAnimations,
                DisableIndividualSounds = defaultPermissions.DisableIndividualSounds,
                DisableIndividualVFX = defaultPermissions.DisableIndividualVFX,
                IndividualIsSticky = defaultPermissions.IndividualIsSticky,
            },
        };
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<bool> CheckClientHealth()
    {
        await UpdateUserOnRedis().ConfigureAwait(false);

        return false;
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnConnectedAsync()
    {
        if (_userConnections.TryGetValue(UserUID, out var oldId))
        {
            _logger.LogCallWarning(MareHubLogger.Args(_contextAccessor.GetIpAddress(), "UpdatingId", oldId, Context.ConnectionId));
            _userConnections[UserUID] = Context.ConnectionId;
        }
        else
        {
            _mareMetrics.IncGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

            try
            {
                _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, UserCharaIdent));
                await _onlineSyncedPairCacheService.InitPlayer(UserUID).ConfigureAwait(false);
                await UpdateUserOnRedis().ConfigureAwait(false);
                _userConnections[UserUID] = Context.ConnectionId;
            }
            catch
            {
                _userConnections.Remove(UserUID, out _);
            }
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        if (_userConnections.TryGetValue(UserUID, out var connectionId)
            && string.Equals(connectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            _mareMetrics.DecGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

            try
            {
                await _onlineSyncedPairCacheService.DisposePlayer(UserUID).ConfigureAwait(false);

                _logger.LogCallInfo(MareHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, UserCharaIdent));
                if (exception != null)
                    _logger.LogCallWarning(MareHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, exception.Message, exception.StackTrace));

                await RemoveUserFromRedis().ConfigureAwait(false);

                _mareCensus.ClearStatistics(UserUID);

                await SendOfflineToAllPairedUsers().ConfigureAwait(false);

                DbContext.RemoveRange(DbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == UserUID));
                await DbContext.SaveChangesAsync().ConfigureAwait(false);

            }
            catch { }
            finally
            {
                _userConnections.Remove(UserUID, out _);
            }
        }
        else
        {
            _logger.LogCallWarning(MareHubLogger.Args(_contextAccessor.GetIpAddress(), "ObsoleteId", UserUID, Context.ConnectionId));
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Notifiy the recipient pair to apply the spesified Moodles we provide from our own moodles list to their status manager.
    /// <para>
    /// NOTICE:
    /// This CAN check for permission validation, and will verify the moodles info fits
    /// the allowed permissions. Will return false if it does not.
    /// </para>
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> UserApplyMoodlesByStatus(ApplyMoodlesByStatusDto dto)
    {
        // // simply validate that they are an existing pair.
        // var pairPerms = await DbContext.ClientPairPermissions.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID && u.OtherUserUID == UserUID).ConfigureAwait(false);
        // if (pairPerms == null)
        // {
        //      await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "Cannot apply moodles to a non-paired user!").ConfigureAwait(false);
        //      return false;
        // }
        // if (!pairPerms.PairCanApplyOwnMoodlesToYou)
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "You do not have permission to apply moodles to this user!").ConfigureAwait(false);
        //     return false;
        // }

        // var moodlesToApply = dto.Statuses;

        // if (moodlesToApply.Any(m => m.Type == StatusType.Positive && !pairPerms.AllowPositiveStatusTypes))
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "One of the Statuses have a positive type, which this pair does not allow!").ConfigureAwait(false);
        //     return false;
        // }
        //
        // if (moodlesToApply.Any(m => m.Type == StatusType.Negative && !pairPerms.AllowNegativeStatusTypes))
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "One of the Statuses have a negative type, which this pair does not allow!").ConfigureAwait(false);
        //     return false;
        // }
        //
        // if (moodlesToApply.Any(m => m.Type == StatusType.Special && !pairPerms.AllowSpecialStatusTypes))
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "One of the Statuses have a special type, which this pair does not allow!").ConfigureAwait(false);
        //     return false;
        // }
        //
        // if (moodlesToApply.Any(m => m.NoExpire && !pairPerms.AllowPermanentMoodles))
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "One of the Statuses is permanent, which this pair does not allow!").ConfigureAwait(false);
        //     return false;
        // }
        //
        // // ensure to only check this condition as one to be flagged if it exceeds the time and is NOT marked as permanent.
        // if (moodlesToApply.Any(m => new TimeSpan(m.Days, m.Hours, m.Minutes, m.Seconds) > pairPerms.MaxMoodleTime && m.NoExpire == false))
        // {
        //     await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, "One of the Statuses exceeds the max allowed time!").ConfigureAwait(false);
        //     return false;
        // }

        // // construct a new dto with the client caller as the user.
        // var newDto = new ApplyMoodlesByStatusDto(User: UserUID.ToUserDataFromUID(), Statuses: moodlesToApply, Type: dto.Type);

        // notify the recipient pair to apply the moodles.
        await Clients.User(dto.User.UID).Client_UserApplyMoodlesByStatus(dto).ConfigureAwait(false);

        return true;
    }
}