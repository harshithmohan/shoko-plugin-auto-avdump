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
        Automatically AVDumps a video when its automatic release search
        fails to recognize it after import.
        """;

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton<AutoAvdumpService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AutoAvdumpService>());
    }
}
