#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.xThemeSong.Models
{
    /// <summary>
    /// Where a theme song suggestion came from.
    /// Serialized by name so both theme.json and the API carry a readable value.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ThemeLookupSource
    {
        /// <summary>Nothing was found.</summary>
        None = 0,

        /// <summary>ThemerrDB supplied a curated theme song URL.</summary>
        ThemerrDb = 1,

        /// <summary>A soundtrack listing supplied track names, searched on YouTube.</summary>
        Soundtrack = 2
    }

    /// <summary>
    /// A single soundtrack entry pulled from a soundtrack listing.
    /// Property names stay PascalCase: these are stored inside theme.json, which is
    /// PascalCase throughout, and the same objects are echoed back by the API.
    /// </summary>
    public class SoundtrackTrack
    {
        /// <summary>Gets or sets the track title.</summary>
        [JsonPropertyName("Title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the performing artist, when the listing names one.</summary>
        [JsonPropertyName("Performer")]
        public string? Performer { get; set; }

        /// <summary>Gets or sets the writer, when the listing names one.</summary>
        [JsonPropertyName("Writer")]
        public string? Writer { get; set; }

        /// <summary>
        /// Gets the search phrase for this track: "title artist" when an artist is known.
        /// </summary>
        public string ToSearchQuery()
        {
            return string.IsNullOrWhiteSpace(Performer) ? Title : $"{Title} {Performer}";
        }
    }

    /// <summary>
    /// The outcome of an automatic theme song lookup for one media item.
    /// Property names are camelCase to match the plugin's other API responses.
    /// </summary>
    public class ThemeLookupResult
    {
        /// <summary>Gets or sets the source that produced the result.</summary>
        [JsonPropertyName("source")]
        public ThemeLookupSource Source { get; set; } = ThemeLookupSource.None;

        /// <summary>Gets or sets the YouTube URL of the suggested theme song.</summary>
        [JsonPropertyName("youTubeUrl")]
        public string? YouTubeUrl { get; set; }

        /// <summary>Gets or sets the YouTube video ID of the suggested theme song.</summary>
        [JsonPropertyName("youTubeId")]
        public string? YouTubeId { get; set; }

        /// <summary>Gets or sets the title of the suggested theme song.</summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>Gets or sets the IMDb ID the lookup ran against.</summary>
        [JsonPropertyName("imdbId")]
        public string? ImdbId { get; set; }

        /// <summary>Gets or sets the TMDB ID the lookup ran against.</summary>
        [JsonPropertyName("tmdbId")]
        public string? TmdbId { get; set; }

        /// <summary>Gets or sets the soundtrack listing found for the item, if any.</summary>
        [JsonPropertyName("soundtrack")]
        public List<SoundtrackTrack> Soundtrack { get; set; } = new List<SoundtrackTrack>();

        /// <summary>Gets a value indicating whether a playable theme song was found.</summary>
        [JsonIgnore]
        public bool HasTheme => !string.IsNullOrEmpty(YouTubeId);
    }
}
