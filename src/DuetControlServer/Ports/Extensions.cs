using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Ports;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add general-purpose I/O functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddPorts(this IServiceCollection services)
        // Shared between M42, M280 and the spindles, which are three outputs driven together
        => services.AddSingleton<GpioManager>();
}
