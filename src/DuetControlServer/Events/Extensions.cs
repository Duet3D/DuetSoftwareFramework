using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Events;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add the event system to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddEvents(this IServiceCollection services)
    {
        return services
            .AddSingleton<EventQueue>()
            .AddSingleton<Utility.IDiagnostics, EventQueue>(services => services.GetRequiredService<EventQueue>())
            .AddSingleton<EventProcessor>()
            .AddHostedService(services => services.GetRequiredService<EventProcessor>());
    }
}
