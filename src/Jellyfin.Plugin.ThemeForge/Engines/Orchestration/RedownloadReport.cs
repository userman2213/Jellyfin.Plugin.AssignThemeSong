using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>What fetching one theme again resulted in.</summary>
public enum RedownloadOutcome
{
    /// <summary>The theme was fetched again and written with the current audio settings.</summary>
    Redone = 0,

    /// <summary>The file no longer matches what ThemeForge wrote, so somebody replaced it by hand and it was left alone.</summary>
    KeptModified = 1,

    /// <summary>The recorded file is no longer on disk.</summary>
    Missing = 2,

    /// <summary>The download or the write failed; the existing theme is still in place.</summary>
    Failed = 3,
}

/// <summary>
/// A summary of one pass over the themes ThemeForge wrote, fetching each one again.
/// </summary>
/// <remarks>
/// Exists so that a change to how themes are written -- turning processing off, or on -- can
/// reach the themes already in the library without throwing away a single decision. States,
/// scores and review verdicts are untouched; only the files change.
/// </remarks>
public sealed class RedownloadReport
{
    /// <summary>Gets or sets when the pass started.</summary>
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the pass finished.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets how many written themes there were to look at.</summary>
    public int Considered { get; set; }

    /// <summary>Gets or sets how many were fetched and written again.</summary>
    public int Redone { get; set; }

    /// <summary>Gets or sets how many were left alone because they had been replaced by hand.</summary>
    public int KeptModified { get; set; }

    /// <summary>Gets or sets how many were recorded but already gone from disk.</summary>
    public int Missing { get; set; }

    /// <summary>Gets or sets how many could not be fetched or written.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets a value indicating whether the pass was cancelled before finishing.</summary>
    public bool WasCancelled { get; set; }

    /// <summary>Gets or sets an error that stopped the whole pass.</summary>
    public string? FatalError { get; set; }

    /// <summary>Gets the first few failures, each as "item: reason".</summary>
    public List<string> Failures { get; } = new();

    /// <summary>Tallies one outcome.</summary>
    /// <param name="outcome">What happened to one theme.</param>
    public void Record(RedownloadOutcome outcome)
    {
        switch (outcome)
        {
            case RedownloadOutcome.Redone:
                Redone++;
                break;
            case RedownloadOutcome.KeptModified:
                KeptModified++;
                break;
            case RedownloadOutcome.Missing:
                Missing++;
                break;
            default:
                Failed++;
                break;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "re-downloaded {0} of {1} themes; {2} kept because they had been replaced by hand; {3} already gone; {4} failed",
            Redone,
            Considered,
            KeptModified,
            Missing,
            Failed);

        if (WasCancelled)
        {
            text += " (cancelled)";
        }

        if (FatalError is not null)
        {
            text += " — stopped early: " + FatalError;
        }

        return text;
    }
}
