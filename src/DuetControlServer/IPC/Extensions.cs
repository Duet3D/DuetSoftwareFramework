using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.IPC;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add IPC functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddIPC(this IServiceCollection services)
    {
        return services
            .AddSingleton<Processors.ProcessorFactory>()
            .AddSingleton<LockManager>()
            .AddHostedService<Server>();
    }
}
