using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Commands;

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
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        return services
            .AddSingleton<CommandFactory>();
    }
}
