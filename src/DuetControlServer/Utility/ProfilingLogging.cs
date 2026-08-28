using Microsoft.Extensions.Logging;

namespace DuetControlServer.Utility;

/// <summary>
/// Logging setup that only a profiling build has anything to do
/// </summary>
internal static class ProfilingLogging
{
    /// <summary>
    /// Report log messages to Tracy as well, in a build made with -p:Profiling=true
    /// </summary>
    /// <param name="logging">Logging builder to add the provider to</param>
    /// <returns>The logging builder</returns>
    /// <remarks>
    /// Called by every host that runs the engine, so that the messages reach the timeline whether
    /// DuetControlServer was started by Program.cs or by the system test bench. This file is
    /// compiled either way, unlike the Profiling folder it reaches into, which is what lets both
    /// hosts make the same call without knowing whether there is a profiler behind it.
    /// </remarks>
    internal static ILoggingBuilder AddTracyIfProfiling(this ILoggingBuilder logging)
    {
#if PROFILING
        logging.AddProvider(new Profiling.TracyLoggerProvider());
#endif
        return logging;
    }
}
