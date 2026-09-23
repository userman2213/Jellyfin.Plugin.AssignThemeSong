#nullable enable

using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.xThemeSong
{
    /// <summary>
    /// Permission mode for theme song management.
    /// </summary>
    public enum ThemePermissionMode
    {
        /// <summary>
        /// Only server administrators can manage theme songs.
        /// </summary>
        AdminsOnly = 0,

        /// <summary>
        /// Administrators and library managers can manage theme songs.
        /// </summary>
        LibraryManagers = 1,

        /// <summary>
        /// All authenticated users can manage theme songs.
        /// </summary>
        Everyone = 2
    }

    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether to overwrite existing theme files.
        /// </summary>
        public bool OverwriteExistingFiles { get; set; }

        /// <summary>
        /// Gets or sets the audio bitrate for downloaded theme songs.
        /// </summary>
        public int AudioBitrate { get; set; } = 192;

        /// <summary>
        /// Gets or sets the custom FFmpeg path. If empty, auto-detection will be used.
        /// </summary>
        public string? FFmpegPath { get; set; }

        /// <summary>
        /// Gets or sets the permission mode for managing theme songs.
        /// </summary>
        public ThemePermissionMode PermissionMode { get; set; } = ThemePermissionMode.LibraryManagers;

        /// <summary>
        /// Gets or sets the list of user IDs with explicit permission to manage themes.
        /// Only used when PermissionMode is set to a custom mode (future feature).
        /// </summary>
        public List<Guid> AllowedUserIds { get; set; } = new List<Guid>();

        /// <summary>
        /// Gets or sets a value indicating whether ThemerrDB is queried for theme songs.
        /// ThemerrDB is the first source in the automatic lookup chain.
        /// </summary>
        public bool EnableThemerrDb { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the soundtrack lookup runs when
        /// ThemerrDB has no theme for an item. Track names found this way are searched
        /// on YouTube to produce theme song candidates.
        /// </summary>
        public bool EnableSoundtrackFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets the OMDb API key (https://www.omdbapi.com/apikey.aspx).
        /// Used to resolve an IMDb ID for items whose Jellyfin metadata does not
        /// already carry one. Optional: items that already have an IMDb ID from
        /// their metadata provider are looked up without it.
        /// </summary>
        public string? OmdbApiKey { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of soundtrack tracks to keep per item.
        /// </summary>
        public int MaxSoundtrackCandidates { get; set; } = 10;

        /// <summary>
        /// Gets or sets a value indicating whether a theme found by the automatic
        /// lookup is downloaded straight away. When false the lookup only records
        /// what it found in theme.json and leaves the download to a later run.
        /// </summary>
        public bool AutoDownloadFoundThemes { get; set; } = true;

        /// <summary>
        /// Gets or sets how many days to wait before looking an item up again after a
        /// lookup found nothing. Keeps the daily task from re-querying the same sources
        /// for every unmatched item on every run. Set to 0 to always retry.
        /// </summary>
        public int LookupRetryDays { get; set; } = 7;

        /// <summary>
        /// Gets or sets a value indicating whether soundtrack listings may be fetched
        /// with a headless browser. IMDb refuses plain HTTP requests from many servers,
        /// and its challenge only clears in a real browser engine. Falls back to a plain
        /// HTTP request when no browser is installed.
        /// </summary>
        public bool UseBrowserForSoundtrack { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the plugin may download its own copy
        /// of Chrome when none is installed. The download is roughly 150-200 MB and is
        /// kept in the plugin's data folder.
        /// </summary>
        public bool AutoDownloadBrowser { get; set; } = true;

        /// <summary>
        /// Gets or sets the path to a Chrome, Chromium or Edge binary.
        /// Leave empty to auto-detect.
        /// </summary>
        public string? BrowserPath { get; set; }

        /// <summary>
        /// Gets or sets how long to let the browser render a page, in seconds.
        /// Clearing the bot check on a cold profile has been measured at over a minute,
        /// so this is generous; once the profile is warm a render takes a few seconds.
        /// </summary>
        public int BrowserTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Gets or sets a value indicating whether to start the browser with its own
        /// sandbox disabled. Chrome's sandbox cannot start inside most container images,
        /// which is how Jellyfin is usually deployed, so this defaults to true.
        /// </summary>
        public bool BrowserDisableSandbox { get; set; } = true;

        /// <summary>
        /// Gets or sets the User-Agent sent with outbound web requests.
        /// Leave empty to use the built-in desktop Chrome user agent.
        /// </summary>
        public string? UserAgent { get; set; }
    }
}
