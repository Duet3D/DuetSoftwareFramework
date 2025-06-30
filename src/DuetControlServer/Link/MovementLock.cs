using System;
using System.Threading.Tasks;
using DuetAPI;

namespace DuetControlServer.Link;

/// <summary>
/// Class representing an acquired movement lock
/// </summary>
/// <param name="channel">Locked code channel</param>
public class MovementLock(CodeChannel channel, Interface iface) : IAsyncDisposable
{
    /// <summary>
    /// Called when this instance is being disposed
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await iface.UnlockAll(channel);
    }
}
