using MareSynchronos.API.Data;
using MareSynchronos.API.Dto;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.SignalR;
using MareSynchronosServer.Hubs;
using MareSynchronosShared.Data;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;

namespace MareSynchronosServer.Services;

public sealed class SystemInfoService : BackgroundService
{
    private readonly MareMetrics _mareMetrics;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;
    private readonly IRedisDatabase _redis;
    public SupporterDto SupportersDto = new([]);
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IConfigurationService<ServerConfiguration> configurationService, IDbContextFactory<MareDbContext> dbContextFactory,
        ILogger<SystemInfoService> logger, IHubContext<MareHub, IMareHub> hubContext, IRedisDatabase redisDb)
    {
        _mareMetrics = mareMetrics;
        _config = configurationService;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _hubContext = hubContext;
        _redis = redisDb;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        _ = PrugeTempGroups(cancellationToken).ConfigureAwait(false);
        _ = UpdateSupporters(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("System Info Service started");
    }

    private async Task PrugeTempGroups(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                // 步骤 1: 一次性获取所有过期的 GroupId
                var expiredGroupIds = await db.PFinder
                    .AsNoTracking()
                    .Where(x => x.EndTime.AddHours(3) < DateTimeOffset.UtcNow && x.HasTempGroup)
                    .Select(x => x.GroupId)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                if (!expiredGroupIds.Any())
                {
                    // 无需清理，等待下一次循环
                    await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation("Found {count} expired group IDs to purge.", expiredGroupIds.Count);

                // 步骤 2: 分别、一次性地加载所有相关的 Groups 和 GroupPairs
                var groupsToPurge = await db.Groups
                    .Where(g => expiredGroupIds.Contains(g.GID))
                    .ToListAsync(ct).ConfigureAwait(false);

                var pairsToPurge = await db.GroupPairs
                    .Where(p => expiredGroupIds.Contains(p.GroupGID)) // 使用外键进行查询
                    .ToListAsync(ct).ConfigureAwait(false);

                // 步骤 3: 在内存中准备通知，按 GroupId 关联数据
                var notificationsToSend = new List<Func<Task>>();

                // 为了高效查找，将 pairs 按 GroupGID 分组
                var pairsByGroupId = pairsToPurge.GroupBy(p => p.GroupGID).ToDictionary(k => k.Key, v => v.ToList());

                foreach (var group in groupsToPurge)
                {
                    // 查找该群组对应的所有配对
                    if (pairsByGroupId.TryGetValue(group.GID, out var pairsForThisGroup))
                    {
                        var userUids = pairsForThisGroup.Select(p => p.GroupUserUID).ToList();
                        if (userUids.Any())
                        {
                            var groupDto = new GroupDto(new GroupData(group.GID, group.Alias));
                            // 准备通知任务，但不立即执行
                            notificationsToSend.Add(() => _hubContext.Clients.Users(userUids).Client_GroupDelete(groupDto));
                        }
                    }
                }

                // 步骤 4: 在单个事务中执行所有数据库Delete操作
                if (pairsToPurge.Any())
                {
                    db.GroupPairs.RemoveRange(pairsToPurge);
                }
                if (groupsToPurge.Any())
                {
                    db.Groups.RemoveRange(groupsToPurge);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Successfully purged {groupCount} groups and {pairCount} pairs from DB.", groupsToPurge.Count, pairsToPurge.Count);

                // 步骤 5: 数据库操作成功后，发送所有 SignalR 通知
                foreach (var notificationTask in notificationsToSend)
                {
                    await notificationTask().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("PrugeTempGroups task was cancelled.");
                break; // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred in PrugeTempGroups.");
            }

            await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        }
    }

    private async Task UpdateSupporters(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested){
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                var combinedQuery =
                    (from support in db.Supports.AsNoTracking()
                        where support.ExpiresAt > DateTime.UtcNow
                        select support.UserUID)
                    .Union(
                        from auth in db.Auth.AsNoTracking()
                        where db.Supports.AsNoTracking()
                            .Where(s => s.ExpiresAt > DateTime.UtcNow)
                            .Select(s => s.UserUID)
                            .Contains(auth.PrimaryUserUID)
                        select auth.User.UID
                    )
                    .Distinct()
                    .ToList();

                combinedQuery.Sort(StringComparer.OrdinalIgnoreCase);

                if (!combinedQuery.SequenceEqual(SupportersDto.Supporters, StringComparer.OrdinalIgnoreCase))
                {
                    SupportersDto = new SupporterDto(combinedQuery);
                    _ = _hubContext.Clients.All.Client_UpdateSupporterList(SupportersDto).ConfigureAwait(false);
                    _logger.LogWarning("Updated Supporter list, count {count}", SupportersDto.Supporters.Count);
                }

                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update supporter info");
            }
        }
        
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timeOut = _config.IsMain ? 15 : 30;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ThreadPool.GetAvailableThreads(out int workerThreads, out int ioThreads);

                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableWorkerThreads, workerThreads);
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAvailableIOWorkerThreads, ioThreads);

                var onlineUsers = (_redis.SearchKeysAsync("UID:*").GetAwaiter().GetResult()).Count();
                SystemInfoDto = new SystemInfoDto()
                {
                    OnlineUsers = onlineUsers,
                };

                if (_config.IsMain)
                {
                    _logger.LogInformation("Sending System Info, Online Users: {onlineUsers}", onlineUsers);

                    await _hubContext.Clients.All.Client_UpdateSystemInfo(SystemInfoDto).ConfigureAwait(false);

                    using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, onlineUsers);
                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairs, db.ClientPairs.AsNoTracking().Count());
                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, db.Permissions.AsNoTracking().Where(p => p.IsPaused).Count());
                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroups, db.Groups.AsNoTracking().Count());
                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairs, db.GroupPairs.AsNoTracking().Count());
                    _mareMetrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, db.Users.AsNoTracking().Count());
                }

                await Task.Delay(TimeSpan.FromSeconds(timeOut), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push system info");
            }
        }
    }
}