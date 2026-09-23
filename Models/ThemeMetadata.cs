#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.xThemeSong.Models
{
    public class ThemeMetadata
    {
        [JsonPropertyName("YouTubeId")]
        public string? YouTubeId { get; set; }

        [JsonPropertyName("YouTubeUrl")]
        public string? YouTubeUrl { get; set; }

        [JsonPropertyName("Title")]
        public string? Title { get; set; }

        [JsonPropertyName("Uploader")]
        public string? Uploader { get; set; }

        [JsonPropertyName("DateAdded")]
        public DateTime DateAdded { get; set; }

        [JsonPropertyName("DateModified")]
        public DateTime DateModified { get; set; }

        [JsonPropertyName("IsUserUploaded")]
        public bool IsUserUploaded { get; set; }

        [JsonPropertyName("OriginalFileName")]
        public string? OriginalFileName { get; set; }

        /// <summary>
        /// Gets or sets where an automatically found theme came from
        /// (ThemerrDB or a soundtrack listing). Null for manual entries.
        /// </summary>
        [JsonPropertyName("Source")]
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets the IMDb ID the automatic lookup ran against.
        /// </summary>
        [JsonPropertyName("ImdbId")]
        public string? ImdbId { get; set; }

        /// <summary>
        /// Gets or sets the soundtrack track names found for this item. Kept so the
        /// listing does not have to be fetched again to offer alternative themes.
        /// </summary>
        [JsonPropertyName("Soundtrack")]
        public List<SoundtrackTrack>? Soundtrack { get; set; }
    }
}
