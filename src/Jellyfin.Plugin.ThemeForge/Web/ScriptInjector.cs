using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Web;

/// <summary>
/// Adds ThemeForge's client script to the Jellyfin web UI by registering a transformation
/// with the File Transformation plugin.
/// </summary>
/// <remarks>
/// <para>
/// The transformation is applied to <c>index.html</c> in memory as it is served. ThemeForge
/// deliberately offers no on-disk fallback that edits Jellyfin's own <c>index.html</c>: that
/// file is replaced on every server update, so a patched copy either reverts silently or
/// leaves a stale script tag behind. If File Transformation is not installed the injected
/// menu entry is simply absent; the configuration page, scheduled tasks and API are unaffected.
/// </para>
/// <para>
/// Every interaction with File Transformation is reflective, including building its JSON
/// payload. That keeps ThemeForge free of a compile-time Newtonsoft.Json reference and avoids
/// the assembly-identity mismatch that arises when two plugins load different copies of it.
/// </para>
/// </remarks>
public static class ScriptInjector
{
    private static string _basePath = string.Empty;
    private static string _version = "0.0.0.0";
    private static ILogger? _logger;

    /// <summary>
    /// Attempts to register the transformation.
    /// </summary>
    /// <param name="pluginId">The plugin's id, reused as the transformation id.</param>
    /// <param name="version">The plugin version, which busts the script cache on upgrade.</param>
    /// <param name="configurationManager">Used to discover the configured reverse-proxy base URL.</param>
    /// <param name="logger">Logger.</param>
    /// <returns><c>true</c> when the transformation was registered.</returns>
    public static bool TryRegister(
        Guid pluginId,
        Version version,
        IServerConfigurationManager configurationManager,
        ILogger logger)
    {
        _logger = logger;
        _version = version.ToString();
        _basePath = ResolveBasePath(configurationManager, logger);

        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains("FileTransformation", StringComparison.OrdinalIgnoreCase) == true);

            if (assembly is null)
            {
                logger.LogInformation(
                    "ThemeForge: the File Transformation plugin is not installed, so the item-page menu entry will not appear. " +
                    "The configuration page, scheduled tasks and API are unaffected.");
                return false;
            }

            var register = assembly
                .GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?.GetMethod("RegisterTransformation");

            if (register is null)
            {
                logger.LogWarning("ThemeForge: File Transformation is installed but its RegisterTransformation entry point was not found; skipping injection.");
                return false;
            }

            var payload = BuildPayload(register, pluginId);
            if (payload is null)
            {
                return false;
            }

            register.Invoke(null, new[] { payload });
            logger.LogInformation("ThemeForge: registered the index.html transformation with the File Transformation plugin.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ThemeForge: failed to register with the File Transformation plugin; the item-page menu entry will not appear.");
            return false;
        }
    }

    /// <summary>
    /// Builds the registration payload as whichever JSON object type File Transformation expects,
    /// by parsing JSON with that type's own <c>Parse</c> method.
    /// </summary>
    private static object? BuildPayload(MethodInfo register, Guid pluginId)
    {
        var parameters = register.GetParameters();
        if (parameters.Length != 1)
        {
            _logger?.LogWarning("ThemeForge: RegisterTransformation has an unexpected signature ({Count} parameters); skipping injection.", parameters.Length);
            return null;
        }

        var payloadType = parameters[0].ParameterType;
        var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        if (parse is null)
        {
            _logger?.LogWarning("ThemeForge: cannot construct a {Type} payload for File Transformation; skipping injection.", payloadType.FullName);
            return null;
        }

        var json = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"id\":\"{0}\",\"fileNamePattern\":\"index.html\",\"callbackAssembly\":\"{1}\",\"callbackClass\":\"{2}\",\"callbackMethod\":\"{3}\"}}",
            pluginId,
            typeof(ScriptInjector).Assembly.FullName,
            typeof(ScriptInjector).FullName,
            nameof(TransformIndexHtml));

        return parse.Invoke(null, new object[] { json });
    }

    /// <summary>
    /// Invoked by the File Transformation plugin for each request for <c>index.html</c>.
    /// </summary>
    /// <param name="payload">The file contents to transform.</param>
    /// <returns>The transformed contents, or the original if anything goes wrong.</returns>
    public static string TransformIndexHtml(PatchRequestPayload payload)
    {
        var content = payload?.Contents ?? string.Empty;
        if (content.Length == 0)
        {
            return content;
        }

        try
        {
            var scriptTag = string.Format(
                CultureInfo.InvariantCulture,
                "<script plugin=\"ThemeForge\" version=\"{1}\" src=\"{0}/ThemeForge/ClientScript\" defer></script>",
                _basePath,
                _version);

            if (content.Contains(scriptTag, StringComparison.Ordinal))
            {
                return content;
            }

            // Drop the tag left by a previous version before inserting the current one.
            content = Regex.Replace(content, "<script plugin=\"ThemeForge\".*?></script>", string.Empty, RegexOptions.IgnoreCase);

            var bodyClose = content.LastIndexOf("</body>", StringComparison.Ordinal);
            if (bodyClose < 0)
            {
                _logger?.LogWarning("ThemeForge: index.html has no closing body tag; serving it unmodified.");
                return content;
            }

            return content.Insert(bodyClose, scriptTag);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ThemeForge: index.html transformation failed; serving the file unmodified.");
            return payload?.Contents ?? string.Empty;
        }
    }

    /// <summary>
    /// Reads Jellyfin's configured base URL so the injected script tag resolves correctly when
    /// the server is hosted under a reverse-proxy sub-path.
    /// </summary>
    private static string ResolveBasePath(IServerConfigurationManager configurationManager, ILogger logger)
    {
        try
        {
            var network = configurationManager.GetConfiguration("network");
            var baseUrl = network?.GetType().GetProperty("BaseUrl")?.GetValue(network)?.ToString()?.Trim('/');
            return string.IsNullOrEmpty(baseUrl) ? string.Empty : "/" + baseUrl;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ThemeForge: could not read the network base URL; assuming the server is at the site root.");
            return string.Empty;
        }
    }
}
