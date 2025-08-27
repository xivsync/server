using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Cache)]
public class CacheController : ControllerBase
{
    private readonly RequestFileStreamResultFactory _requestFileStreamResultFactory;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ILogger<CacheController> logger,
        RequestFileStreamResultFactory requestFileStreamResultFactory,
        CachedFileProvider cachedFileProvider,
        RequestQueueService requestQueue,
        FileStatisticsService fileStatisticsService) : base(logger)
    {
        _logger = logger;
        _requestFileStreamResultFactory = requestFileStreamResultFactory;
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
        _fileStatisticsService = fileStatisticsService;
    }

    // GET /cache/get?requestId=...
    [HttpGet(MareFiles.Cache_Get)]
    public async Task<IActionResult> GetFiles(Guid requestId)
    {
        var sw = Stopwatch.StartNew();
        var user = MareUser ?? "<null>";
        _logger.LogDebug("GetFiles: ENTER user={User}, requestId={RequestId}", user, requestId);

        try
        {
            // 1) validate request state
            if (!_requestQueue.IsActiveProcessing(requestId, user, out var request))
            {
                _logger.LogWarning("GetFiles: request NOT active. user={User}, requestId={RequestId}",
                    user, requestId);
                return BadRequest($"Request {requestId} is not active for user {user}");
            }

            // 2) mark active and log details
            _requestQueue.ActivateRequest(requestId);
            var fileCount = request.FileIds?.Count ?? 0;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"GetFiles: request active. user={user}, requestId={requestId}, count={fileCount}");
                foreach (var h in request.FileIds.Take(20))
                    sb.AppendLine($"  - {h}");
                if (fileCount > 20) sb.AppendLine($"  ... +{fileCount - 20} more");
                _logger.LogDebug("{Details}", sb.ToString());
            }

            Response.ContentType = "application/octet-stream";

            long requestSize = 0;
            var substreams = new List<BlockFileDataSubstream>(Math.Max(fileCount, 0));

            // 3) fetch each file via CachedFileProvider
            var perFileTimer = new Stopwatch();

            foreach (var fileHash in request.FileIds)
            {
                perFileTimer.Restart();
                _logger.LogDebug("GetFiles: begin file user={User}, requestId={RequestId}, hash={Hash}",
                    user, requestId, fileHash);

                // NOTE: This replaces any old TryDownloadAsync/_config usage
                var fi = await _cachedFileProvider.DownloadAndGetLocalFileInfo(fileHash).ConfigureAwait(false);

                if (fi == null)
                {
                    _logger.LogWarning("GetFiles: file NOT FOUND after fetch. user={User}, requestId={RequestId}, hash={Hash}, elapsedMs={Elapsed}",
                        user, requestId, fileHash, perFileTimer.ElapsedMilliseconds);
                    continue;
                }

                substreams.Add(new BlockFileDataSubstream(fi));
                requestSize += fi.Length;

                _logger.LogDebug("GetFiles: file READY user={User}, requestId={RequestId}, hash={Hash}, length={Length}, elapsedMs={Elapsed}",
                    user, requestId, fileHash, fi.Length, perFileTimer.ElapsedMilliseconds);
            }

            // 4) aggregate logging
            _logger.LogInformation("GetFiles: aggregated user={User}, requestId={RequestId}, filesReady={Count}, totalBytes={Bytes}, totalMs={Elapsed}",
                user, requestId, substreams.Count, requestSize, sw.ElapsedMilliseconds);

            if (substreams.Count == 0)
            {
                _logger.LogWarning("GetFiles: no substreams created. user={User}, requestId={RequestId}. Returning empty payload.",
                    user, requestId);
            }

            _fileStatisticsService.LogRequest(requestSize);

            // 5) stream the combined result
            _logger.LogDebug("GetFiles: RETURN streaming result user={User}, requestId={RequestId}, contentType={ContentType}",
                user, requestId, Response.ContentType);

            return _requestFileStreamResultFactory.Create(requestId, new BlockFileDataStream(substreams));
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogWarning(oce, "GetFiles: CANCELLED user={User}, requestId={RequestId}, elapsedMs={Elapsed}",
                user, requestId, sw.ElapsedMilliseconds);
            return BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFiles: ERROR user={User}, requestId={RequestId}, elapsedMs={Elapsed}",
                user, requestId, sw.ElapsedMilliseconds);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            _logger.LogDebug("GetFiles: EXIT user={User}, requestId={RequestId}, totalMs={Elapsed}",
                user, requestId, sw.ElapsedMilliseconds);
        }
    }
}
