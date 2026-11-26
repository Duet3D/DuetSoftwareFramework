using System;

namespace DuetControlServer.Link.Native;

/// <summary>
/// DuetControlServer's dedicated threads taking part in the activity count of DuetRealtimeCore
/// (<c>Platform/Activity.h</c>), which a bench that drives the SBC's clock itself waits on before it
/// moves the clock. A thread that takes part counts as running except while it waits in the library.
/// Thread-pool work is watched through the thread pool instead
/// </summary>
internal static class NativeActivity
{
    /// <summary>Count the calling thread as running until the result is disposed</summary>
    public static RunningScope Running()
    {
        NativeMethods.DuetRT_ActivityThreadStarted();
        return new RunningScope();
    }

    /// <summary>A thread counted as running</summary>
    public readonly ref struct RunningScope : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => NativeMethods.DuetRT_ActivityThreadStopped();
    }
}
