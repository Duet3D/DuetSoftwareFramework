using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Codes;

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
    public static IServiceCollection AddCodes(this IServiceCollection services)
    {
        return services
            .AddSingleton<Meta.Expressions>()
            .AddHostedService<Meta.Functions>()
            .AddSingleton<CodeFactory>()
            .AddSingleton<CodeProcessor>()
            .AddSingleton<IDiagnostics, CodeProcessor>(services => services.GetRequiredService<CodeProcessor>())
            .AddHostedService<CodeProcessorService>()
            .AddKeyedSingleton<Handlers.ICodeHandler, Handlers.GCodeHandler>(Handlers.Keys.GCodes)
            .AddKeyedSingleton<Handlers.ICodeHandler, Handlers.MCodeHandler>(Handlers.Keys.MCodes)
            .AddKeyedSingleton<Handlers.ICodeHandler, Handlers.TCodeHandler>(Handlers.Keys.TCodes)
            .AddKeyedSingleton<Handlers.ICodeHandler, Handlers.KeywordHandler>(Handlers.Keys.Keywords);
    }
}
