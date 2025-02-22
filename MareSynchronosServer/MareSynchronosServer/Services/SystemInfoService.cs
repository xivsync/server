using MareSynchronos.API.Dto;
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

public sealed class SystemInfoService : IHostedService, IDisposable
{
    private readonly MareMetrics _mareMetrics;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<SystemInfoService> _logger;
    private readonly IHubContext<MareHub, IMareHub> _hubContext;
    private readonly IRedisDatabase _redis;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private Timer _timer;
    private Timer _timer2;
    public SupporterDto Supporters = new([]);
    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public SystemInfoService(MareMetrics mareMetrics, IConfigurationService<ServerConfiguration> configurationService, IServiceProvider services,
        ILogger<SystemInfoService> logger, IHubContext<MareHub, IMareHub> hubContext, IRedisDatabase redisDb, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _mareMetrics = mareMetrics;
        _config = configurationService;
        _services = services;
        _logger = logger;
        _hubContext = hubContext;
        _redis = redisDb;
        _dbContextFactory = dbContextFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("System Info Service started");

        var timeOut = _config.IsMain ? 5 : 15;

        _timer = new Timer(PushSystemInfo, null, TimeSpan.Zero, TimeSpan.FromSeconds(timeOut));
        _timer2 = new Timer(UpdateSupporters, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));

        return Task.CompletedTask;
    }

    private void UpdateSupporters(object state)
    {
        try
        {
            using var db = _dbContextFactory.CreateDbContext();

            var combinedQuery =
                (from support in db.Supports.AsNoTracking()
                    where support.ExpiresAt > DateTime.Now
                    select support.UserUID)
                .Union(
                    from auth in db.Auth.AsNoTracking()
                    where db.Supports.AsNoTracking()
                        .Where(s => s.ExpiresAt > DateTime.Now)
                        .Select(s => s.UserUID)
                        .Contains(auth.PrimaryUserUID)
                    select auth.User.UID
                )
                .Distinct()
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            var sortedDistinctUserUids = combinedQuery.ToList();

            if (sortedDistinctUserUids.SequenceEqual(Supporters.Supporters.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                return;

            Supporters = new SupporterDto(sortedDistinctUserUids);
            _ = _hubContext.Clients.All.Client_UpdateSupporterList(Supporters).ConfigureAwait(false);
            _logger.LogWarning("Updated Supporter list, count {count}", Supporters.Supporters.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update supporter info");
        }
    }

    private void PushSystemInfo(object state)
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

                _hubContext.Clients.All.Client_UpdateSystemInfo(SystemInfoDto);

                using var scope = _services.CreateScope();
                using var db = scope.ServiceProvider.GetService<MareDbContext>()!;

                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeAuthorizedConnections, onlineUsers);
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairs, db.ClientPairs.AsNoTracking().Count());
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, db.Permissions.AsNoTracking().Count(p => p.IsPaused));
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroups, db.Groups.AsNoTracking().Count());
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeGroupPairs, db.GroupPairs.AsNoTracking().Count());
                _mareMetrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, db.Users.AsNoTracking().Count());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push system info");
        }
    }


    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        _timer2?.Change(Timeout.Infinite, 0);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer2?.Dispose();
    }
}