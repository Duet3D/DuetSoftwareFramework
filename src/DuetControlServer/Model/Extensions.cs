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
            .AddSingleton<Observer>()
            .AddHostedService<Observer>(provider => provider.GetRequiredService<Observer>())
            .AddHostedService<PeriodicUpdateService>()
            .AddHostedService<UpdateService>();
    }
}
