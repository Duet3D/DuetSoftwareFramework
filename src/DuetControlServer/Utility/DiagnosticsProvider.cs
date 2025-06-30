using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace DuetControlServer.Utility;

/// <summary>
/// Attribute to indicate the priority of a diagnostics provider
/// </summary>
/// <param name="priority">Priority of the diagnostics provider. Default is 0</param>
[AttributeUsage(AttributeTargets.Class)]
public class DiagnosticsPriorityAttribute(int priority) : Attribute
{
    /// <summary>
    /// Priority of the diagnostics provider
    /// </summary>
    public int DiagnosticsPriority { get; } = priority;
}

/// <summary>
/// Interface for synchronous diagnostics providers
/// </summary>
public interface IDiagnostics
{
    /// <summary>
    /// Print diagnostics
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    void PrintDiagnostics(StringBuilder builder);
}

/// <summary>
/// Interface for asynchronous diagnostics providers
/// </summary>
public interface IAsyncDiagnostics
{
    /// <summary>
    /// Print diagnostics asynchronously
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken);
}

/// <summary>
/// Diagnostics provider
/// </summary>
/// <param name="serviceProvider">Service provider</param>
public class DiagnosticsProvider(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Fixed timeout for asynchronous diagnostic calls
    /// </summary>
    private const int DiagnosticsTimeout = 5000;

    /// <summary>
    /// Get ordered list of diagnostics providers
    /// </summary>
    /// <param name="serviceProvider">Service provider</param>
    /// <returns>List of diagnostics providers</returns>
    private IEnumerable<object> GetDiagnosticsProviders()
    {
        Dictionary<object, int> diagnosticProviders = [];
        foreach (var provider in serviceProvider.GetServices<IDiagnostics>())
        {
            int priority = ((DiagnosticsPriorityAttribute)Attribute.GetCustomAttribute(provider.GetType(), typeof(DiagnosticsPriorityAttribute))!)?.DiagnosticsPriority ?? 0;
            diagnosticProviders.Add(provider, priority);
        }
        foreach (var provider in serviceProvider.GetServices<IDiagnostics>())
        {
            int priority = ((DiagnosticsPriorityAttribute)Attribute.GetCustomAttribute(provider.GetType(), typeof(DiagnosticsPriorityAttribute))!)?.DiagnosticsPriority ?? 0;
            diagnosticProviders.Add(provider, priority);
        }
        return diagnosticProviders.OrderBy(item => item.Value).Select(item => item.Key);
    }

    /// <summary>
    /// Print diagnostics of all registered diagnostic providers
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection</returns>
    public async ValueTask<string> PrintAsync()
    {
        BuildDateTimeAttribute buildAttribute = (BuildDateTimeAttribute)Attribute.GetCustomAttribute(System.Reflection.Assembly.GetExecutingAssembly(), typeof(BuildDateTimeAttribute))!;

        StringBuilder builder = new();
        builder.AppendLine("=== Duet Control Server ===");
        builder.AppendLine($"Duet Control Server version {Program.Version} ({buildAttribute.Date ?? "unknown build time"}, {(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");
        foreach (object provider in GetDiagnosticsProviders())
        {
            if (provider is IDiagnostics syncProvider)
            {
                syncProvider.PrintDiagnostics(builder);
            }
            if (provider is IAsyncDiagnostics asyncProvider)
            {
                using CancellationTokenSource cts = new(DiagnosticsTimeout);
                try
                {
                    await asyncProvider.PrintDiagnosticsAsync(builder, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    builder.AppendLine($"Diagnostics for {provider.GetType().Name} timed out after {DiagnosticsTimeout}ms");
                }
            }
        }
        return builder.ToString();
    }
}
