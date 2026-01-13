using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // Initialize static loggers
        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        Processors.CodeInterception.SetLogger(loggerFactory.CreateLogger<Processors.CodeInterception>());
        
        return services
            .AddSingleton<Processors.ProcessorFactory>()
            .AddSingleton<LockManager>()
            .AddSingleton<Server>()
            .AddHostedService(services => services.GetRequiredService<Server>());
    }
}
