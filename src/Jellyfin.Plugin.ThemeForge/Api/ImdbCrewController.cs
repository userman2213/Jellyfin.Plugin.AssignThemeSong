using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>
/// Shows the state of the local copy of IMDb's crew data, and downloads a new one on request.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ThemeForge/ImdbCrew")]
[Produces(MediaTypeNames.Application.Json)]
public class ImdbCrewController : ControllerBase
{
    private readonly IImdbCrewData _crew;
    private readonly IThemeForgeLogger<ImdbCrewController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImdbCrewController"/> class.</summary>
    /// <param name="crew">The local copy.</param>
    /// <param name="logger">Logger.</param>
    public ImdbCrewController(IImdbCrewData crew, IThemeForgeLogger<ImdbCrewController> logger)
    {
        _crew = crew;
        _logger = logger;
    }

    /// <summary>Describes the local copy.</summary>
    /// <returns>Its state.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ImdbCrewStatus> Get() => _crew.Status(Plugin.Config);

    /// <summary>Starts downloading a new copy, whether or not one is due.</summary>
    /// <remarks>
    /// Returns at once: the download is about 1.2 GB and takes minutes. Its progress is the
    /// <c>Refreshing</c> flag of <see cref="Get"/>, and its outcome is logged.
    /// </remarks>
    /// <returns>Whether a download was started.</returns>
    [HttpPost("Refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<OperationResult> Refresh()
    {
        var status = _crew.Status(Plugin.Config);
        if (!status.Enabled)
        {
            return Conflict(new OperationResult(false, "Using IMDb's crew data is switched off."));
        }

        if (status.Refreshing)
        {
            return Conflict(new OperationResult(false, "IMDb's crew data is already being downloaded."));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _crew.RefreshAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ThemeForge: the download of IMDb's crew data asked for on the settings page failed.");
            }
        });

        return Accepted(new OperationResult(true, "Downloading IMDb's crew data, about 1.2 GB. This takes a few minutes; the status below updates when it is done."));
    }
}
