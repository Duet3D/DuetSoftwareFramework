using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DuetEmulation.Emulator;

/// <summary>
/// Starts processes from one thread that lives as long as this process. A child started with a
/// parent-death signal (setpriv --pdeathsig) gets it when the thread that forked it exits, not the
/// process, and a thread-pool thread can be retired at any time
/// </summary>
internal static class ProcessLauncher
{
    private static readonly BlockingCollection<(ProcessStartInfo StartInfo, TaskCompletionSource<Process> Started)> Requests = [];

    static ProcessLauncher()
    {
        new Thread(() =>
        {
            foreach ((ProcessStartInfo startInfo, TaskCompletionSource<Process> started) in Requests.GetConsumingEnumerable())
            {
                try
                {
                    started.SetResult(Process.Start(startInfo) ?? throw new RenodeException($"Could not start {startInfo.FileName}"));
                }
                catch (System.Exception e)
                {
                    started.SetException(e);
                }
            }
        })
        { IsBackground = true, Name = "Process launcher" }.Start();
    }

    /// <summary>Start a process from the launcher thread</summary>
    public static Task<Process> StartAsync(ProcessStartInfo startInfo)
    {
        TaskCompletionSource<Process> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add((startInfo, started));
        return started.Task;
    }
}
