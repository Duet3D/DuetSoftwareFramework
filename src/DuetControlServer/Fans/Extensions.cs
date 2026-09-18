using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Fans;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add fan functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddFans(this IServiceCollection services)
        // Shared between the M-codes that create and drive the fans and the tool subsystem, which
        // addresses a tool's own fans
        => services.AddSingleton<FanManager>();
}
