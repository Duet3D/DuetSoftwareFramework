using System;
using Microsoft.Extensions.DependencyInjection;
using DuetControlServer.Utility;

namespace DuetControlServer.Link;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add link functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddLink(this IServiceCollection services)
    {
        return services
            .AddSingleton<Channel.Manager>();
    }

    /// <summary>
    /// Add link adapter to the service collection based on configured communication method
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddLinkAdapter(this IServiceCollection services)
    {
        // Determine which communication method to use
        var serviceProvider = services.BuildServiceProvider();
        var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Settings>>().Value;

        if (settings.CommunicationMethod.Equals("usb", StringComparison.OrdinalIgnoreCase))
        {
            return services
                .AddSingleton<Adapter.USB>()
                .AddSingleton<Adapter.ILinkAdapter, Adapter.USB>(services => services.GetRequiredService<Adapter.USB>())
                .AddSingleton<IDiagnostics, Adapter.USB>(services => services.GetRequiredService<Adapter.USB>())
                .AddSingleton<LinkInterface>()
                .AddHostedService<LinkService>();
        }
        else
        {
            return services
                .AddSingleton<Adapter.SPI>()
                .AddSingleton<Adapter.ILinkAdapter, Adapter.SPI>(services => services.GetRequiredService<Adapter.SPI>())
                .AddSingleton<IDiagnostics, Adapter.SPI>(services => services.GetRequiredService<Adapter.SPI>())
                .AddSingleton<LinkInterface>()
                .AddHostedService<LinkService>();
        }
    }
}
