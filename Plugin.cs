using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;

namespace Shoko.Plugin.AutoAVDump;

/// <summary>
///   Automatically runs AniDB AVDump for videos that an automatic release
///   search could not recognize, so a later search can match them.
/// </summary>
public class Plugin : IPlugin, IPluginServiceRegistration
{
    /// <summary>
    ///   The plugin's identity,
    ///   <c>UuidUtility.GetV5("Shoko.Plugin.AutoAVDump.Plugin")</c>.
    /// </summary>
    public Guid ID { get; private init; } = new("5c522f64-6057-5b7e-b19d-aada4a097318");

    /// <inheritdoc/>
    public string Name { get; private set; } = "Auto AVDump";

    /// <inheritdoc/>
    public string Description { get; private set; } = """
        When the server's automatic release search cannot recognize a newly imported
        video, this plugin dumps it through the built-in AVDump: the file's hashes
        and technical metadata are registered with AniDB, which is the first of the
        two steps of file registration (adding the dumped file to a series/episode
        is still manual). It needs no settings and no user interaction: failed searches are
        batched and dumped automatically, a failed dump is retried up to three times,
        and the plugin then stops trying for that video.
        """;

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton<AutoAvdumpService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AutoAvdumpService>());
    }
}
