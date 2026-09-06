using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Jellyfin.Plugin.ThemeForge.Logging;
using Jellyfin.Plugin.ThemeForge.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.ThemeForge;

/// <summary>
/// Wires the engines together.
/// </summary>
/// <remarks>
/// Every engine is registered against its interface, which is what lets each one be replaced or
/// tested without the others knowing. The scoring rules are registered as a collection so adding
/// a new rule is a matter of adding one line here and nothing else.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Logging. Every engine takes IThemeForgeLogger<T> rather than ILogger<T>, so its
        // output reaches both Jellyfin's log and ThemeForge's own file.
        serviceCollection.AddSingleton<IThemeForgeLogSink>(ThemeForgeLogFile.Shared);
        serviceCollection.AddSingleton(typeof(IThemeForgeLogger<>), typeof(ThemeForgeLogger<>));

        // Tooling
        serviceCollection.AddSingleton<IProcessRunner, ProcessRunner>();
        serviceCollection.AddSingleton<IFfmpegLocator, FfmpegLocator>();
        serviceCollection.AddSingleton<IToolProvisioner, YtDlpProvisioner>();

        // Pipeline engines
        serviceCollection.AddSingleton<IMediaIdentityResolver, MediaIdentityResolver>();
        serviceCollection.AddSingleton<IQueryPlanner, QueryPlanner>();
        serviceCollection.AddSingleton<ICandidateSource, YtDlpCandidateSource>();

        // Catalogues keyed on the item's own database id, asked before any searching. Registered
        // as a collection, and each one decides for itself whether the user has enabled it.
        serviceCollection.AddSingleton<IThemeProvenanceSource, ThemerrDbSource>();
        serviceCollection.AddSingleton<IThemeProvenanceSource, PlexTvThemeSource>();
        serviceCollection.AddSingleton<IDecisionPolicy, DecisionPolicy>();
        serviceCollection.AddSingleton<IAudioProbe, AudioProbe>();
        serviceCollection.AddSingleton<IAudioVerifier, AudioVerifier>();
        serviceCollection.AddSingleton<ILoudnessNormalizer, LoudnessNormalizer>();
        serviceCollection.AddSingleton<IAcquisitionEngine, YtDlpAcquisitionEngine>();
        serviceCollection.AddSingleton<IThemePlacementEngine, ThemePlacementEngine>();
        serviceCollection.AddSingleton<ILibraryPolicyResolver, LibraryPolicyResolver>();
        serviceCollection.AddSingleton<IThemeIndex, JsonThemeIndex>();

        // Scoring rules, collected into the engine.
        serviceCollection.AddSingleton<IScoringRule, TitleSimilarityRule>();
        serviceCollection.AddSingleton<IScoringRule, KeywordAffinityRule>();
        serviceCollection.AddSingleton<IScoringRule, NegativeKeywordRule>();
        serviceCollection.AddSingleton<IScoringRule, DurationPlausibilityRule>();
        serviceCollection.AddSingleton<IScoringRule, ChannelReputationRule>();
        serviceCollection.AddSingleton<IScoringRule, PopularityRule>();
        serviceCollection.AddSingleton<IScoringRule, RecencyRule>();
        serviceCollection.AddSingleton<IScoringRule, AvailabilityRule>();
        serviceCollection.AddSingleton<IScoringRule, DuplicateRule>();
        serviceCollection.AddSingleton<IScoringRule, QuerySpecificityRule>();
        serviceCollection.AddSingleton<IScoringEngine, ScoringEngine>();

        serviceCollection.AddSingleton<IThemeOrchestrator, ThemeOrchestrator>();

        // Scheduled work and library events
        serviceCollection.AddSingleton<IScheduledTask, DiscoverThemesTask>();
        serviceCollection.AddSingleton<IScheduledTask, UpdateYtDlpTask>();
        serviceCollection.AddHostedService<NewItemWatcher>();
    }
}
