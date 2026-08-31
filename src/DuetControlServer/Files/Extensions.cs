using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // Initialize static loggers
        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        Parser.ImageProcessing.IconImageParser.SetLogger(loggerFactory.CreateLogger<Parser.FileInfoParser>());
        
        return services
            .AddSingleton<Parser.FileInfoParser>()
            .AddSingleton<FilePathResolver>()
            .AddSingleton<Job.JobController>()
            .AddSingleton<IAsyncDiagnostics, Job.JobController>(services => services.GetRequiredService<Job.JobController>())
            .AddSingleton<Job.JobSequences>()
            .AddSingleton<FileFactory>()
            .AddSingleton<MacroRunner>()
            .AddSingleton<JobMonitor>()
            .AddHostedService((provider) => provider.GetRequiredService<Job.JobController>())
            .AddHostedService((provider) => provider.GetRequiredService<JobMonitor>());
    }
}
