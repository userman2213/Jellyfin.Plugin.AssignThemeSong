using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>
/// Serves the script injected into the Jellyfin web UI.
/// </summary>
/// <remarks>
/// Anonymous by necessity: the browser fetches this from a script tag in <c>index.html</c>, which
/// is served before the user has authenticated. The file is static JavaScript containing no
/// configuration or data, and every action it can trigger goes through the administrator-only
/// API, so serving it unauthenticated discloses nothing.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("ThemeForge")]
public class ClientScriptController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.ThemeForge.Web.clientScript.js";

    /// <summary>Serves the client script.</summary>
    /// <returns>The JavaScript, or 404 when it is missing from the assembly.</returns>
    [HttpGet("ClientScript")]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Content(reader.ReadToEnd(), "application/javascript");
    }
}
