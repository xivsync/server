using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MareSynchronos.API.Routes;
using MareSynchronosStaticFilesServer.Services;
using MareSynchronosStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Request)]
public class RequestController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;

    public RequestController(
        ILogger<RequestController> logger,
        CachedFileProvider cachedFileProvider,
        RequestQueueService requestQueue) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
    }

    [HttpGet]
    [Route(MareFiles.Request_Cancel)]
    public async Task<IActionResult> CancelQueueRequest(Guid requestId)
    {
        try
        {
            _requestQueue.RemoveFromQueue(requestId, MareUser, IsPriority);
            return Ok();
        }
        catch (OperationCanceledException)
        {
            return BadRequest();
        }
    }

    // NOTE: We eagerly download files here so CacheController can serve them immediately.
    [HttpPost]
    [Route(MareFiles.Request_Enqueue)]
    public async Task<IActionResult> PreRequestFilesAsync([FromBody] IEnumerable<string> files)
    {
        try
        {
            var list = (files ?? Enumerable.Empty<string>()).ToList();

            foreach (var file in list)
            {
                _logger.LogDebug("Prerequested file: {File}", file);
                await _cachedFileProvider.DownloadFileWhenRequired(file).ConfigureAwait(false);
            }

            var requestId = Guid.NewGuid();
            var req = new UserRequest(requestId, MareUser, list);

            await _requestQueue.EnqueueUser(req, IsPriority, HttpContext.RequestAborted)
                               .ConfigureAwait(false);

            return Ok(requestId);
        }
        catch (OperationCanceledException)
        {
            return BadRequest();
        }
    }

    [HttpGet]
    [Route(MareFiles.Request_Check)]
    public async Task<IActionResult> CheckQueueAsync(Guid requestId, [FromBody] IEnumerable<string>? files)
    {
        try
        {
            if (!_requestQueue.StillEnqueued(requestId, MareUser, IsPriority))
            {
                var list = (files ?? Enumerable.Empty<string>()).ToList();
                var req = new UserRequest(requestId, MareUser, list);
                await _requestQueue.EnqueueUser(req, IsPriority, HttpContext.RequestAborted)
                                   .ConfigureAwait(false);
            }

            return Ok();
        }
        catch (OperationCanceledException)
        {
            return BadRequest();
        }
    }
}
