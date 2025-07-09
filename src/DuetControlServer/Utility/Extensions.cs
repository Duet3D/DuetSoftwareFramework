using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Utility;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add utility functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddUtility(this IServiceCollection services)
    {
        return services
            .AddSingleton<DiagnosticsProvider>()
            .AddSingleton<FirmwareUpdater>()
            .AddSingleton<EventLogger>()
            .AddSingleton<MQTT>();
    }
}
