﻿using MareSynchronos.API.Dto;
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
        _logger.LogInformation("System Info Service started");
    }

    private override async Task UpdateSupporters(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested){
        try
        {
            using var db = _dbContextFactory.CreateDbContext();

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