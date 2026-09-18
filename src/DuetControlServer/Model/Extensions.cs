using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Model;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add command functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddModel(this IServiceCollection services)
    {
        return services
            .AddSingleton<Filter>()
            .AddSingleton<ObjectModel>()
            .AddHostedService<MachineStatusService>()
            .AddSingleton<IDiagnostics, ObjectModel>(services => services.GetRequiredService<ObjectModel>())
            .AddSingleton<Observer>()
            .AddSingleton<PeriodicUpdateService>()
            .AddSingleton<SbcTriggerService>()
            .AddHostedService(provider => provider.GetRequiredService<Observer>())
            .AddHostedService(provider => provider.GetRequiredService<PeriodicUpdateService>())
            .AddHostedService(provider => provider.GetRequiredService<SbcTriggerService>());
    }
}
