#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.xThemeSong.Models;
using Jellyfin.Plugin.xThemeSong.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Api
{
    /// <summary>
    /// Administrator diagnostics for the soundtrack lookup, so the settings page can
    /// show whether this particular server can actually reach IMDb.
    /// </summary>
    [ApiController]
    [Route("xThemeSong/diagnostics")]
    public class DiagnosticsController : ControllerBase
    {
        /// <summary>
        /// Fight Club, used as the default probe: it has a long soundtrack listing, so
        /// an empty result means the fetch failed rather than the title having no music.
        /// </summary>
        private const string DefaultProbeImdbId = "tt0137523";

        private readonly ILogger<DiagnosticsController> _logger;
        private readonly SoundtrackLookupService _soundtrackLookup;
        private readonly BrowserPageFetcher _browser;
        private readonly ChromiumProvisioner _provisioner;

        public DiagnosticsController(
            ILogger<DiagnosticsController> logger,
            SoundtrackLookupService soundtrackLookup,
            BrowserPageFetcher browser,
            ChromiumProvisioner provisioner)
        {
            _logger = logger;
            _soundtrackLookup = soundtrackLookup;
            _browser = browser;
            _provisioner = provisioner;
        }

        /// <summary>
        /// Reports whether a browser is available, without downloading one.
        /// </summary>
        [HttpGet("browser")]
        public async Task<ActionResult> GetBrowserStatus(CancellationToken cancellationToken)
        {
            if (!User.IsInRole("Administrator"))
            {
                return Forbid();
            }

            var path = _browser.ResolveBrowserPath();

            return Ok(new
            {
                available = path != null,
                path,
                source = _browser.DescribeBrowserSource(),
                version = path == null
                    ? null
                    : await _browser.GetBrowserVersionAsync(cancellationToken).ConfigureAwait(false),
                platform = ChromiumProvisioner.GetPlatformKey(),
                autoDownloadEnabled = Plugin.Instance?.Configuration?.AutoDownloadBrowser ?? true
            });
        }

        /// <summary>
        /// Downloads Chrome into the plugin's data folder if it is not already there.
        /// </summary>
        [HttpPost("browser/install")]
        public async Task<ActionResult> InstallBrowser(CancellationToken cancellationToken)
        {
            if (!User.IsInRole("Administrator"))
            {
                return Forbid();
            }

            _logger.LogInformation("Browser installation requested from the settings page");

            var result = await _provisioner.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                success = result.Success,
                alreadyInstalled = result.WasAlreadyInstalled,
                path = result.ExecutablePath,
                version = result.Version,
                downloadedMb = result.DownloadedBytes > 0
                    ? Math.Round(result.DownloadedBytes / 1024d / 1024d, 1)
                    : 0d,
                error = result.Error,
                message = result.Success
                    ? result.WasAlreadyInstalled
                        ? "A browser is already installed."
                        : $"Installed Chrome {result.Version}."
                    : result.Error
            });
        }

        /// <summary>
        /// Removes the browser the plugin downloaded, freeing the disk space.
        /// </summary>
        [HttpDelete("browser")]
        public ActionResult RemoveBrowser()
        {
            if (!User.IsInRole("Administrator"))
            {
                return Forbid();
            }

            var dataPath = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(dataPath))
            {
                return BadRequest("The plugin has no data folder.");
            }

            var installRoot = Path.Combine(dataPath, "chromium");
            if (!Directory.Exists(installRoot))
            {
                return Ok(new { removed = false, message = "No downloaded browser to remove." });
            }

            try
            {
                Directory.Delete(installRoot, recursive: true);
                _logger.LogInformation("Removed the downloaded browser at {Path}", installRoot);
                return Ok(new { removed = true, message = "Removed the downloaded browser." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not remove the downloaded browser");
                return StatusCode(500, new { removed = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Runs a real IMDb soundtrack pull and reports each step.
        /// </summary>
        /// <param name="imdbId">Optional IMDb ID to test with.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [HttpPost("imdb-test")]
        public async Task<ActionResult<SoundtrackDiagnostic>> TestImdbPull(
            [FromQuery] string? imdbId,
            CancellationToken cancellationToken)
        {
            if (!User.IsInRole("Administrator"))
            {
                return Forbid();
            }

            var probeId = string.IsNullOrWhiteSpace(imdbId) ? DefaultProbeImdbId : imdbId!.Trim();

            if (!IsPlausibleImdbId(probeId))
            {
                return BadRequest("That does not look like an IMDb ID. Expected a value such as tt0137523.");
            }

            _logger.LogInformation("Running an IMDb soundtrack test for {ImdbId}", probeId);

            try
            {
                var diagnostic = await _soundtrackLookup
                    .RunDiagnosticAsync(probeId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("IMDb test finished: {Summary}", diagnostic.Summary);
                return Ok(diagnostic);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The IMDb test failed");
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks the shape of an IMDb ID before it is put into a URL.
        /// </summary>
        private static bool IsPlausibleImdbId(string value)
        {
            if (value.Length < 3 || value.Length > 12
                || !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var i = 2; i < value.Length; i++)
            {
                if (!char.IsAsciiDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
