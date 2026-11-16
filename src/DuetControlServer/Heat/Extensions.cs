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
        => services
            // Shared between the M-codes that configure and drive the heaters and the tool subsystem,
            // which brings a tool's heaters to temperature when it is selected
            .AddSingleton<HeatManager>()
            // Shared between M302, which configures it, and the move path, which every extruder movement is checked by
            .AddSingleton<ColdExtrusionInterlock>()
            // Shared between M308, which configures the sensors, and the boards' reports, which
            // read them; it also runs the sweep that times out a sensor whose readings stop
            .AddSingleton<SensorManager>()
            .AddHostedService(services => services.GetRequiredService<SensorManager>());
}
