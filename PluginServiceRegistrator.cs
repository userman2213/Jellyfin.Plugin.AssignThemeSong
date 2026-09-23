using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.xThemeSong.Services;

namespace Jellyfin.Plugin.xThemeSong
{
    /// <summary>
    /// Register xThemeSong services.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<BrowserHttpClient>();
            serviceCollection.AddSingleton<LookupRetryCache>();
            serviceCollection.AddSingleton<ThemerrDbService>();
            serviceCollection.AddSingleton<SoundtrackLookupService>();
            serviceCollection.AddSingleton<ThemeResolverService>();
            serviceCollection.AddSingleton<ThemeDownloadService>();
            serviceCollection.AddSingleton<IScheduledTask, ThemeSongTask>();
            // Note: StartupService removed - registration now happens in Plugin constructor
        }
    }
}
