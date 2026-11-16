using System.Threading;
using Microsoft.Extensions.Hosting;

namespace UnitTests.Utility;

/// <summary>
/// An application lifetime that never fires, which is all the object model and the event logger need to be constructed
/// </summary>
internal sealed class StoppedLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
