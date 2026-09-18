using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Heat;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add heat functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddHeat(this IServiceCollection services)
        // Shared between the M-codes that configure and drive the heaters and the tool subsystem,
        // which brings a tool's heaters to temperature when it is selected
        => services.AddSingleton<HeatManager>();
}
