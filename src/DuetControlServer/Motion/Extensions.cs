using DuetAPI.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using DuetControlServer.Utility;

namespace DuetControlServer.Motion;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add motion functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddMotion(this IServiceCollection services)
    {
        // Determine which communication method to use
        var serviceProvider = services.BuildServiceProvider();
        var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Settings>>().Value;

        return services
            // Shared between the link dispatcher, which records what the engine reports, and the
            // motion service, which acts on it
            .AddSingleton<MotionTracker>()
            // Where G-codes become queued moves. Shared with the code handlers, which is the whole
            // point of it: every move has to be built in the order it was commanded
            .AddSingleton<MovePlanner>()
            // The height map in effect. Shared between the codes that load it and the move builder
            // that applies it, which is why it is not simply a field of either
            .AddSingleton<BedCompensation>()
            .AddHostedService<MotionService>();
    }
}
