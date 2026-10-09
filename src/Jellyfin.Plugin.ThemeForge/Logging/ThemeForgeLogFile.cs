using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Logging;

/// <summary>
/// Appends ThemeForge's own log lines to a rotating file, separate from Jellyfin's log.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated file exists because a full library run produces thousands of lines that are only
/// meaningful together — which candidate won, what each rule scored, why an item was skipped —
/// and finding them interleaved with everything else the server logs is impractical. Everything
/// written here is <em>also</em> written to Jellyfin's log at its normal level, so this adds a
/// view rather than hiding anything.
/// </para>
/// <para>
/// Writing is deliberately synchronous and flushed per line. The log's main job is explaining a
/// run that went wrong, and a buffered writer loses exactly the lines that matter when the
/// process dies. At a few lines per library item the cost is irrelevant.
/// </para>
/// <para>
/// Nothing here throws. A logger that can take down the operation it is describing is worse
/// than no logger.
/// </para>
/// </remarks>
public interface IThemeForgeLogSink
{
    /// <summary>Writes one entry.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="category">The logger category, usually a fully-qualified type name.</param>
    /// <param name="message">The formatted message.</param>
    /// <param name="exception">An exception to append, if any.</param>
    void Write(LogLevel level, string category, string message, Exception? exception);
}

/// <inheritdoc cref="IThemeForgeLogSink" />
public sealed class ThemeForgeLogFile : IThemeForgeLogSink, IDisposable
{
    private static readonly Lazy<ThemeForgeLogFile> LazyShared = new(() => new ThemeForgeLogFile());

    private readonly object _gate = new();
    private readonly string? _directoryOverride;

    private StreamWriter? _writer;
    private string? _path;
    private long _written;
    private bool _disposed;
    private bool _failed;

    /// <summary>Initializes a new instance of the <see cref="ThemeForgeLogFile"/> class.</summary>
    /// <param name="directoryOverride">
    /// Where to write. Left null in normal use so the location follows the plugin's data
    /// directory; supplied by tests so they neither need a running plugin nor share one file.
    /// </param>
    public ThemeForgeLogFile(string? directoryOverride = null) => _directoryOverride = directoryOverride;

    /// <summary>Gets the process-wide sink.</summary>
    /// <remarks>
    /// A singleton because it owns one file handle. <see cref="Plugin"/> is constructed by
    /// Jellyfin's plugin loader rather than by dependency injection, so it needs a way to reach
    /// the same sink the injected loggers use.
    /// </remarks>
    public static ThemeForgeLogFile Shared => LazyShared.Value;

    /// <summary>Gets the directory the plugin's logs live in, or null before it has initialised.</summary>
    public static string? DefaultLogDirectory =>
        Plugin.Instance is { DataPath: var data } && !string.IsNullOrEmpty(data)
            ? Path.Combine(data, "logs")
            : null;

    /// <summary>Gets the directory this sink writes to, or null when there is nowhere to write yet.</summary>
    private string? LogDirectory => _directoryOverride ?? DefaultLogDirectory;

    /// <summary>Gets the path of the current log file, or null if logging has not started.</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _path;
            }
        }
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var configuration = Plugin.Config;

        // A sink with an explicit directory is under test and writes regardless of the saved
        // configuration, which belongs to a plugin instance that does not exist there.
        if (_directoryOverride is null
            && (!configuration.EnableFileLogging || level < configuration.FileLogLevel || level == LogLevel.None))
        {
            return;
        }

        var line = Format(level, category, message);

        lock (_gate)
        {
            if (_disposed || _failed)
            {
                return;
            }

            try
            {
                EnsureOpen(configuration.MaxLogFileSizeMb, configuration.MaxLogFiles);
                if (_writer is null)
                {
                    return;
                }

                _writer.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;

                if (exception is not null)
                {
                    var detail = exception.ToString();
                    _writer.WriteLine(detail);
                    _written += detail.Length + Environment.NewLine.Length;
                }
            }
            catch (Exception)
            {
                // Give up permanently rather than throwing on every subsequent line. Jellyfin's
                // own log still has everything; only this convenience view is lost.
                _failed = true;
                CloseWriter();
            }
        }
    }

    /// <summary>Reads the most recent lines, for the diagnostics view in the settings page.</summary>
    /// <param name="lines">How many lines to return.</param>
    /// <returns>The tail of the current log, oldest first, or an empty array when there is none.</returns>
    public string[] Tail(int lines)
    {
        var path = CurrentPath;
        if (path is null || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            lock (_gate)
            {
                _writer?.Flush();
            }

            // Shared read access: the writer holds this file open.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new string[Math.Clamp(lines, 1, 5000)];
            var count = 0;
            var total = 0;

            while (reader.ReadLine() is { } line)
            {
                buffer[count] = line;
                count = (count + 1) % buffer.Length;
                total++;
            }

            if (total <= buffer.Length)
            {
                return buffer[..total];
            }

            // The ring wrapped, so re-order it oldest-first.
            var result = new string[buffer.Length];
            for (var i = 0; i < buffer.Length; i++)
            {
                result[i] = buffer[(count + i) % buffer.Length];
            }

            return result;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    private static string Format(LogLevel level, string category, string message) => string.Format(
        CultureInfo.InvariantCulture,
        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}: {3}",
        DateTime.Now,
        Abbreviate(level),
        ShortCategory(category),
        message);

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    /// <summary>Trims the namespace off a category, which is always this plugin's own.</summary>
    private static string ShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }

    /// <summary>Opens the log if needed, rotating first when the current file has grown too large.</summary>
    private void EnsureOpen(int maxSizeMb, int maxFiles)
    {
        var directory = LogDirectory;
        if (directory is null)
        {
            // The plugin has not finished constructing; the caller's line goes to Jellyfin's log only.
            return;
        }

        var target = Path.Combine(directory, "themeforge.log");

        if (_writer is not null && string.Equals(_path, target, StringComparison.Ordinal))
        {
            var limit = Math.Max(1, maxSizeMb) * 1024L * 1024L;
            if (_written < limit)
            {
                return;
            }

            CloseWriter();
            Rotate(target, Math.Max(1, maxFiles));
        }

        Directory.CreateDirectory(directory);

        var info = new FileInfo(target);
        _written = info.Exists ? info.Length : 0;
        _writer = new StreamWriter(new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true,
        };
        _path = target;
    }

    /// <summary>Shifts themeforge.log to themeforge.1.log, and so on, discarding the oldest.</summary>
    private static void Rotate(string target, int maxFiles)
    {
        try
        {
            var oldest = ArchiveName(target, maxFiles);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = maxFiles - 1; i >= 1; i--)
            {
                var from = ArchiveName(target, i);
                if (File.Exists(from))
                {
                    File.Move(from, ArchiveName(target, i + 1), overwrite: true);
                }
            }

            if (File.Exists(target))
            {
                File.Move(target, ArchiveName(target, 1), overwrite: true);
            }
        }
        catch (IOException)
        {
            // Rotation is housekeeping. If it fails the log simply keeps growing, which is
            // better than losing the ability to log at all.
        }
    }

    private static string ArchiveName(string target, int index) =>
        Path.ChangeExtension(target, string.Format(CultureInfo.InvariantCulture, "{0}.log", index));

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down a log writer.
        }

        _writer = null;
    }
}
