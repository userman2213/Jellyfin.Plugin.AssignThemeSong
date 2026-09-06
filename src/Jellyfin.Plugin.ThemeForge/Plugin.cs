using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Logging;
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

    private readonly IThemeForgeLogger<Plugin> _logger;

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

        // Wrapped so that startup — tool provisioning, path problems — lands in ThemeForge's own
        // log too. Instance is assigned first because the sink resolves its directory from it.
        _logger = new ThemeForgeLogger<Plugin>(logger);

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

        SweepStagingDirectory();

        _logger.LogInformation("ThemeForge {Version} initialised; data directory {Path}.", Version, DataPath);

        ScriptInjector.TryRegister(Id, Version, configurationManager, _logger);
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

    /// <summary>
    /// Reads the configuration back from the file Jellyfin wrote, bypassing the in-memory copy.
    /// </summary>
    /// <remarks>
    /// <see cref="Config"/> returns the object the plugin is holding, which is the same object a
    /// save was handed, so comparing a save against it proves only that assignment works. This
    /// reads what is actually on disk, which is what survives a restart — so a save that never
    /// reached the disk (a read-only configuration directory, a full disk, a property
    /// <c>XmlSerializer</c> cannot round-trip) is reported at the moment it fails instead of
    /// looking like success until the setting quietly reverts.
    /// </remarks>
    /// <returns>The configuration as stored, or <see langword="null"/> if it could not be read.</returns>
    public PluginConfiguration? ReadPersistedConfiguration()
    {
        try
        {
            if (!File.Exists(ConfigurationFilePath))
            {
                return null;
            }

            return XmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath)
                as PluginConfiguration;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not read the saved configuration back from {Path}.", ConfigurationFilePath);
            return null;
        }
    }

    /// <summary>
    /// Removes everything ThemeForge has written outside the plugin folder, just before Jellyfin
    /// uninstalls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin's uninstall deletes the plugin's own directory and nothing else, so without this
    /// the index, the logs and a roughly 30 MB yt-dlp binary would be left behind indefinitely
    /// with nothing to indicate what they belonged to.
    /// </para>
    /// <para>
    /// Theme files already written into the media library are deliberately left alone. They are
    /// the user's media now, and uninstalling a plugin must never delete a user's files as a
    /// side effect — the settings page offers an explicit action for anyone who does want them
    /// removed.
    /// </para>
    /// </remarks>
    public override void OnUninstalling()
    {
        try
        {
            ThemeForgeLogFile.Shared.Dispose();

            if (Directory.Exists(DataPath))
            {
                Directory.Delete(DataPath, recursive: true);
                _logger.LogInformation("ThemeForge: removed its data directory {Path}. Theme files in your library were left untouched.", DataPath);
            }
        }
        catch (Exception ex)
        {
            // Throwing here would fail the uninstall itself, which is a far worse outcome than
            // leaving a directory behind for the user to delete.
            _logger.LogError(ex, "ThemeForge: could not remove {Path} during uninstall; it can be deleted by hand.", DataPath);
        }

        base.OnUninstalling();
    }

    /// <summary>
    /// Clears anything left in the staging directory by a previous process.
    /// </summary>
    /// <remarks>
    /// An acquisition either completes and publishes its file or has its directory removed, so
    /// nothing under staging is ever meaningful across a restart. Without this sweep, every
    /// download interrupted by a crash or a container restart leaves a directory that nothing
    /// would ever clean up.
    /// </remarks>
    private void SweepStagingDirectory()
    {
        try
        {
            if (!Directory.Exists(StagingPath))
            {
                return;
            }

            var swept = 0;
            foreach (var directory in Directory.EnumerateDirectories(StagingPath))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    swept++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "ThemeForge: could not remove the stale staging directory {Path}.", directory);
                }
            }

            if (swept > 0)
            {
                _logger.LogInformation("ThemeForge: cleared {Count} staging directories left by a previous run.", swept);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not sweep the staging directory.");
        }
    }

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
