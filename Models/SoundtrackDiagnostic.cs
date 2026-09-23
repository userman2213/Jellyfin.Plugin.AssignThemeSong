#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.xThemeSong.Models
{
    /// <summary>
    /// A step in the soundtrack fetch chain, reported to the settings page so an
    /// administrator can see exactly where a lookup succeeded or stalled.
    /// </summary>
    public class DiagnosticStep
    {
        /// <summary>Gets or sets the step name.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the step succeeded.</summary>
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        /// <summary>Gets or sets what happened.</summary>
        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;

        /// <summary>Gets or sets how long the step took.</summary>
        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// The result of the settings page's "Test IMDb pull" action.
    /// </summary>
    public class SoundtrackDiagnostic
    {
        /// <summary>Gets or sets whether tracks were pulled.</summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        /// <summary>Gets or sets the IMDb ID that was tested.</summary>
        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; } = string.Empty;

        /// <summary>Gets or sets a one-line verdict for the administrator.</summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>Gets or sets where the browser came from.</summary>
        [JsonPropertyName("browserSource")]
        public string? BrowserSource { get; set; }

        /// <summary>Gets or sets the browser executable in use.</summary>
        [JsonPropertyName("browserPath")]
        public string? BrowserPath { get; set; }

        /// <summary>Gets or sets the browser version string.</summary>
        [JsonPropertyName("browserVersion")]
        public string? BrowserVersion { get; set; }

        /// <summary>Gets or sets the total time taken.</summary>
        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }

        /// <summary>Gets or sets the steps that were run, in order.</summary>
        [JsonPropertyName("steps")]
        public List<DiagnosticStep> Steps { get; set; } = new List<DiagnosticStep>();

        /// <summary>Gets or sets the tracks that were pulled.</summary>
        [JsonPropertyName("tracks")]
        public List<SoundtrackTrack> Tracks { get; set; } = new List<SoundtrackTrack>();
    }
}
