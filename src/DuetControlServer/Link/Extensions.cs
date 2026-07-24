using DuetAPI.ObjectModel;
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
            .AddSingleton<Channel.Manager>()
            .AddSingleton<IAsyncDiagnostics, Channel.Manager>(services => services.GetRequiredService<Channel.Manager>());
    }

    /// <summary>
    /// Add the link transport to the service collection
    /// </summary>
    /// <remarks>
    /// The SPI protocol lives in native code (<c>src/DuetSbcInterface</c>, built as
    /// <c>libduet_sbc.so</c>) so its transfer loop can run on a pinned real-time thread.
    /// <see cref="Native.NativeLink"/> is the managed side of that boundary
    /// </remarks>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddLinkAdapter(this IServiceCollection services)
    {
        return services
            .AddSingleton<Native.NativeLink>()
            .AddSingleton<LinkInterface>()
            .AddSingleton<IDiagnostics, LinkInterface>(services => services.GetRequiredService<LinkInterface>())
            .AddHostedService<LinkService>();
    }
}
