using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Link;

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
    public static IServiceCollection AddLink(this IServiceCollection services)
    {
        return services
            .AddSingleton<Channel.Manager>();
    }

    /// <summary>
    /// Add command functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddSPILink(this IServiceCollection services)
    {
        return services
            .AddSingleton<Adapter.SPI>()
            .AddSingleton<Adapter.ILinkAdapter, Adapter.SPI>(services => services.GetRequiredService<Adapter.SPI>())
            .AddSingleton<LinkInterface>()
            .AddHostedService<LinkService>();
    }
}
