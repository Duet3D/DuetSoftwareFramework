using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DuetSharedLibrary;

/// <summary>
/// Helper class to run long-running privileged commands as transient systemd units
/// </summary>
/// <remarks>
/// A package operation may replace the very service that started it, and whoever stops that service takes
/// its whole process tree with it. Handing the command to systemd instead puts it in its own unit and
/// cgroup, so it outlives the caller and can still be tracked once the caller comes back up
/// </remarks>
public static class TransientUnit
{
    /// <summary>
    /// Path to the utility that runs a command as a transient unit
    /// </summary>
    private const string SystemdRun = "/usr/bin/systemd-run";

    /// <summary>
    /// Path to the utility that queries unit state
    /// </summary>
    private const string Systemctl = "/usr/bin/systemctl";

    /// <summary>
    /// Run a command as a transient systemd unit
    /// </summary>
    /// <param name="unit">Name of the transient unit without the .service suffix</param>
    /// <param name="fileName">Absolute path of the file to execute</param>
    /// <param name="arguments">Command-line arguments</param>
    /// <param name="redirectOutput">Whether stdout and stderr are to be redirected</param>
    /// <param name="environment">Environment variables to pass to the unit</param>
    /// <returns>Process representing the unit, exiting with the same code as the command</returns>
    /// <exception cref="IOException">Unit could not be started</exception>
    /// <remarks>
    /// The returned process only tracks the unit, so killing it leaves the unit running.
    /// Units are started with --collect so a previous failure does not block the name
    /// </remarks>
    public static Process Start(string unit, string fileName, string arguments, bool redirectOutput = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        string setEnv = string.Empty;
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> variable in environment)
            {
                setEnv += $"--setenv={variable.Key}={variable.Value} ";
            }
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = SystemdRun,
            Arguments = $"--collect --wait --pipe --quiet --unit={unit} {setEnv}{fileName} {arguments}",
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };
        return Process.Start(startInfo) ?? throw new IOException($"Failed to start {unit}");
    }

    /// <summary>
    /// Check if a transient unit is still running
    /// </summary>
    /// <param name="unit">Name of the transient unit without the .service suffix</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the unit is active</returns>
    public static async Task<bool> IsActiveAsync(string unit, CancellationToken cancellationToken = default)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Systemctl, $"is-active --quiet {unit}"));
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
