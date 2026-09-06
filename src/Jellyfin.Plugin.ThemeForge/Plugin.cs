using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge;

/// <summary>
/// ThemeForge: finds, scores and assigns theme songs for movies and series.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's unique identifier.</summary>
    public static readonly Guid PluginId = Guid.Parse("bfb42f2f-93f8-4d6b-97e6-1750035c211f");

    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's path provider.</param>
    /// <param name="xmlSerializer">Serializer used for the plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="configurationManager">Server configuration, used to resolve the base URL.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        IServerConfigurationManager configurationManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;

        DataPath = Path.Combine(applicationPaths.DataPath, "themeforge");
        ToolsPath = Path.Combine(DataPath, "tools");
        StagingPath = Path.Combine(DataPath, "staging");
        IndexPath = Path.Combine(DataPath, "index.json");

        try
        {
            Directory.CreateDirectory(ToolsPath);
            Directory.CreateDirectory(StagingPath);
        }
        catch (Exception ex)
        {
            // A plugin that cannot create its data directory is badly broken, but throwing here
            // would stop Jellyfin loading it at all and hide the reason from the user.
            _logger.LogError(ex, "ThemeForge: could not create the plugin data directory at {Path}.", DataPath);
        }

        _logger.LogInformation("ThemeForge {Version} initialised; data directory {Path}.", Version, DataPath);

        ScriptInjector.TryRegister(Id, Version, configurationManager, logger);
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "ThemeForge";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Automatically finds, scores and assigns theme songs for movies and TV shows using yt-dlp, " +
        "normalising every theme to a consistent loudness.";

    /// <summary>
    /// Gets the directory holding the index, the provisioned tools and the staging area.
    /// Anchored to Jellyfin's data path rather than the version-scoped plugin folder so the
    /// index survives plugin upgrades.
    /// </summary>
    public string DataPath { get; }

    /// <summary>Gets the directory holding the provisioned yt-dlp binary.</summary>
    public string ToolsPath { get; }

    /// <summary>Gets the directory downloads are assembled in before being published to the library.</summary>
    public string StagingPath { get; }

    /// <summary>Gets the path of the index document.</summary>
    public string IndexPath { get; }

    /// <summary>
    /// Gets the current configuration, falling back to defaults when the plugin has not
    /// finished constructing. Callers on background threads can race plugin startup.
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}
