using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Static helper class for wrapping prioritized threads as tasks
    /// </summary>
    public static class PriorityThreadRunner
    {
        /// <summary>
        /// Function to wrap synchronous threads without allocating an extra task
        /// </summary>
        /// <param name="start">Function to start</param>
        /// <param name="priority">Thread priority</param>
        /// <returns>Asynchronous task</returns>
        public static Task Start(ThreadStart start, ThreadPriority priority)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread wrapper = new(() =>
            {
                try
                {
                    start();
                    tcs.SetResult();
                }
                catch (Exception e)
                {
                    if (e is AggregateException ae)
                    {
                        if (ae.InnerException is OperationCanceledException)
                        {
                            tcs.SetCanceled();
                        }
                        else
                        {
                            tcs.SetException(ae.InnerException!);
                        }
                    }
                    else if (e is OperationCanceledException)
                    {
                        tcs.SetCanceled();
                    }
                    else
                    {
                        tcs.SetException(e);
                    }
                }
            })
            {
                Priority = priority,
                IsBackground = true
            };
            wrapper.Start();
            return tcs.Task;
        }
    }
}
