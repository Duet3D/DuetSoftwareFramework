using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Files;

/// <summary>
/// Extensions for the service collection
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Add G-code file functionality to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public static IServiceCollection AddFiles(this IServiceCollection services)
    {
        return services
            .AddSingleton<Parser.FileInfoParser>()
            .AddSingleton<FilePathResolver>()
            .AddSingleton<JobProcessor>()
            .AddSingleton<FileFactory>()
            .AddHostedService((provider) => provider.GetRequiredService<JobProcessor>());
    }
}
