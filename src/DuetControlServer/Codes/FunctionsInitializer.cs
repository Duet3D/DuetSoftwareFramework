using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes;

/// <summary>
/// Hosted service that ensures Functions is instantiated at startup to register custom expression functions
/// </summary>
/// <param name="functions">Functions instance to ensure initialization</param>
internal sealed class FunctionsInitializer(Meta.Functions functions) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Functions is injected, which ensures it's instantiated and custom functions are registered
        _ = functions;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
