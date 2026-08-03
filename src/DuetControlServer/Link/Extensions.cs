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
    /// <remarks>
    /// The per-channel firmware state this used to register is gone: codes are executed here rather
    /// than handed to a firmware, so the code pipeline is the only channel state there is
    /// </remarks>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddLink(this IServiceCollection services) => services;

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
            .AddSingleton<Expansion.ExpansionBoardManager>()
            .AddHostedService(services => services.GetRequiredService<Expansion.ExpansionBoardManager>())
            .AddSingleton<LinkInterface>()
            .AddSingleton<IDiagnostics, LinkInterface>(services => services.GetRequiredService<LinkInterface>())
            .AddHostedService<LinkService>();
    }
}
