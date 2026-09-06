using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>The outcome of running an external tool.</summary>
/// <param name="ExitCode">The process exit code, or -1 if it never started.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
/// <param name="TimedOut">Whether the process was killed for exceeding its time budget.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    /// <summary>Gets a value indicating whether the process completed successfully.</summary>
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>Gets the last few lines of stderr, for log messages that should stay readable.</summary>
    public string ErrorSummary
    {
        get
        {
            if (TimedOut)
            {
                return "timed out";
            }

            var lines = StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var take = Math.Min(3, lines.Length);
            return take == 0 ? $"exit code {ExitCode}" : string.Join(" | ", lines[^take..]).Trim();
        }
    }
}

/// <summary>Runs an external command line tool.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs a tool to completion, capturing its output.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed individually so the runtime handles quoting.</param>
    /// <param name="timeout">How long to wait before killing the process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured result.</returns>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// The single place ThemeForge starts a child process.
/// </summary>
/// <remarks>
/// Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/> rather than being
/// concatenated into a command line, so titles containing quotes, ampersands or spaces cannot
/// break the invocation or be interpreted by a shell. Output is drained on background tasks
/// while the process runs, because a tool that fills the stderr pipe blocks forever if nobody
/// is reading it.
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="ProcessRunner"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ProcessRunner(ILogger<ProcessRunner> logger) => _logger = logger;

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("ThemeForge: running {File} {Args}", fileName, string.Join(' ', arguments));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ThemeForge: could not start {File}.", fileName);
            return new ProcessResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false),
                TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The time budget expired rather than the caller cancelling.
            TryKill(process, fileName);
            _logger.LogWarning("ThemeForge: {File} exceeded its {Timeout:g} time budget and was terminated.", fileName, timeout);
            return new ProcessResult(-1, Drain(stdout), Drain(stderr), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, fileName);
            throw;
        }
    }

    private void TryKill(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not terminate {File}.", fileName);
        }
    }

    /// <summary>Recovers whatever a reader captured before it was cancelled.</summary>
    private static string Drain(Task<string> reader)
    {
        try
        {
            return reader.IsCompletedSuccessfully ? reader.Result : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
