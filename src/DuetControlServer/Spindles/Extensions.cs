using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Spindles;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add spindle functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddSpindles(this IServiceCollection services)
        => services.AddSingleton<SpindleManager>();
}
