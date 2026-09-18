using System;
using System.Globalization;
using System.Net.Http;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Says who is calling.
/// </summary>
/// <remarks>
/// Both of the databases consulted here require it, and both say so plainly: Wikimedia answers a
/// request with no informative User-Agent with 403 and a note that such scripts "may be blocked
/// without notice", and MusicBrainz throttles anonymous callers harder. Naming the plugin and
/// linking to it is also simply the courteous thing to do when using somebody's free service.
/// </remarks>
public static class PoliteRequest
{
    /// <summary>Where to find out what this is, for an administrator of a service we are calling.</summary>
    private const string Home = "https://github.com/userman2213/Jellyfin.Plugin.AssignThemeSong";

    /// <summary>Gets the User-Agent sent with every request to a public database.</summary>
    public static string UserAgent { get; } = string.Format(
        CultureInfo.InvariantCulture,
        "ThemeForge/{0} (+{1})",
        Plugin.Instance?.Version.ToString() ?? "dev",
        Home);

    /// <summary>Adds the User-Agent to a request.</summary>
    /// <param name="request">The request about to be sent.</param>
    public static void Identify(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
    }
}
