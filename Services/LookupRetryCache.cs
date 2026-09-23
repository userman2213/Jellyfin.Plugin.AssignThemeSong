#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Remembers which items a lookup already failed for, so the daily task does not
    /// re-query ThemerrDB and the soundtrack listing for every unmatched item on every
    /// run. Repeatedly scraping the same pages is both wasteful and a good way to get
    /// the server's IP address blocked.
    ///
    /// Kept in the plugin's own data folder rather than in the media library, and only
    /// ever holds failures: a successful lookup writes a theme.json and is never retried.
    /// </summary>
    public class LookupRetryCache
    {
        private const string FileName = "lookup-cache.json";

        private readonly ILogger<LookupRetryCache> _logger;
        private readonly object _lock = new object();

        private Dictionary<string, DateTime>? _entries;

        public LookupRetryCache(ILogger<LookupRetryCache> logger)
        {
            _logger = logger;
        }

        private string? GetCachePath()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dataPath) ? null : Path.Combine(dataPath, FileName);
        }

        /// <summary>
        /// Returns true when this item was looked up unsuccessfully within the retry
        /// window and should be left alone for now.
        /// </summary>
        public bool ShouldSkip(Guid itemId, int retryDays)
        {
            if (retryDays <= 0)
            {
                return false;
            }

            lock (_lock)
            {
                Load();

                if (_entries != null
                    && _entries.TryGetValue(itemId.ToString("N"), out var lastAttempt)
                    && DateTime.UtcNow - lastAttempt < TimeSpan.FromDays(retryDays))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Records that a lookup for this item found nothing.
        /// </summary>
        public void RecordFailure(Guid itemId)
        {
            lock (_lock)
            {
                Load();
                _entries ??= new Dictionary<string, DateTime>();
                _entries[itemId.ToString("N")] = DateTime.UtcNow;
                Save();
            }
        }

        /// <summary>
        /// Drops the record for an item, so the next run looks it up again.
        /// </summary>
        public void Clear(Guid itemId)
        {
            lock (_lock)
            {
                Load();
                if (_entries != null && _entries.Remove(itemId.ToString("N")))
                {
                    Save();
                }
            }
        }

        /// <summary>
        /// Reads the cache file once per process. Callers hold <see cref="_lock"/>.
        /// </summary>
        private void Load()
        {
            if (_entries != null)
            {
                return;
            }

            var path = GetCachePath();
            if (path == null || !File.Exists(path))
            {
                _entries = new Dictionary<string, DateTime>();
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                _entries = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json)
                    ?? new Dictionary<string, DateTime>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the lookup cache at {Path}, starting empty", path);
                _entries = new Dictionary<string, DateTime>();
            }
        }

        /// <summary>
        /// Persists the cache. Callers hold <see cref="_lock"/>.
        /// </summary>
        private void Save()
        {
            var path = GetCachePath();
            if (path == null || _entries == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(_entries));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write the lookup cache to {Path}", path);
            }
        }
    }
}
