using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Judges the uploader.
/// </summary>
/// <remarks>
/// YouTube auto-generates a channel named "&lt;Artist&gt; - Topic" for tracks delivered through
/// music distributors. Those uploads come from the rights holder rather than a fan, which makes
/// them both the correct recording and unlikely to disappear — the best available proxy for an
/// official source.
/// </remarks>
public sealed class ChannelReputationRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "ChannelReputation";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.ChannelReputation;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        var channel = candidate.Channel;
        var channelId = candidate.ChannelId;

        if (string.IsNullOrWhiteSpace(channel) && string.IsNullOrWhiteSpace(channelId))
        {
            return RuleVerdict.Abstain("uploader unknown");
        }

        var configuration = context.Configuration;

        if (Matches(configuration.BlockedChannels, channel, channelId))
        {
            return RuleVerdict.Veto(string.Format(CultureInfo.InvariantCulture, "\"{0}\" is on the blocked channel list", channel));
        }

        if (Matches(configuration.PreferredChannels, channel, channelId))
        {
            return new RuleVerdict(1.0, string.Format(CultureInfo.InvariantCulture, "\"{0}\" is a preferred channel", channel));
        }

        if (configuration.TrustTopicChannels
            && channel is not null
            && channel.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
        {
            return new RuleVerdict(0.8, "auto-generated music channel, so the upload came from the rights holder");
        }

        if (channel is not null && LooksOfficial(channel))
        {
            return new RuleVerdict(0.5, string.Format(CultureInfo.InvariantCulture, "\"{0}\" looks like an official channel", channel));
        }

        return RuleVerdict.Abstain(string.Format(CultureInfo.InvariantCulture, "\"{0}\" is an unknown channel", channel));
    }

    private static bool Matches(System.Collections.Generic.IEnumerable<string>? patterns, string? channel, string? channelId) =>
        patterns?.Any(pattern =>
            !string.IsNullOrWhiteSpace(pattern)
            && ((channel is not null && channel.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                || string.Equals(pattern, channelId, StringComparison.OrdinalIgnoreCase))) == true;

    private static bool LooksOfficial(string channel) =>
        channel.Contains("official", StringComparison.OrdinalIgnoreCase)
        || channel.Contains("records", StringComparison.OrdinalIgnoreCase)
        || channel.Contains("soundtrack", StringComparison.OrdinalIgnoreCase);
}
