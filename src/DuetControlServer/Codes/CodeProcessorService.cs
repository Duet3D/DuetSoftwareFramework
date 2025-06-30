
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace DuetControlServer.Codes;

/// <summary>
/// Service implementation of the code processor
/// </summary>
/// <param name="codeProcessor">Code processor</param>
public class CodeProcessorService(CodeProcessor codeProcessor) : BackgroundService
{
    /// <summary>
    /// Task representing the lifecycle of this class
    /// </summary>
    /// <returns>Asynchronous task</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(codeProcessor.Processors.Select(processor => processor.ExecuteAsync()));
}
