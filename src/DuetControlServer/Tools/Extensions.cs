using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Tools;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add tool functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddTools(this IServiceCollection services)
        // Shared between the T-codes that select a tool, the M-codes that define one, and the move
        // pipeline that asks the selected one for its offsets on every move
        => services.AddSingleton<ToolManager>();
}
