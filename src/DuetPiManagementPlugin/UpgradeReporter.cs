using DuetAPIClient;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Helper class to report the progress of a software upgrade to DCS
    /// </summary>
    /// <param name="connection">Connection to report the progress on</param>
    public sealed partial class UpgradeReporter(BaseCommandConnection connection)
    {
        /// <summary>
        /// Progress file written by unattended-upgrade
        /// </summary>
        private const string UnattendedUpgradeProgressFile = "/var/run/unattended-upgrades.progress";

        /// <summary>
        /// Interval between two checks of the unattended-upgrade progress file (in ms)
        /// </summary>
        private const int ProgressFilePollInterval = 500;

        /// <summary>
        /// Minimum interval between two progress reports (in ms)
        /// </summary>
        private const int MinReportInterval = 250;

        /// <summary>
        /// Smallest progress change worth reporting
        /// </summary>
        private const float MinProgressDelta = 0.005f;

        [GeneratedRegex(@"([0-9.]+)\s*%\s*\((.*)\)")]
        private static partial Regex _unattendedUpgradeProgressRegex();

        private float _phaseStart, _phaseEnd, _phaseSplit;
        private string _message = string.Empty;
        private float _progress;
        private long _lastReport = long.MinValue;

        /// <summary>
        /// Start a new upgrade phase whose own progress is mapped to the given overall range
        /// </summary>
        /// <param name="message">Description of the new phase</param>
        /// <param name="start">Overall progress at the start of the phase (0..1)</param>
        /// <param name="end">Overall progress at the end of the phase (0..1)</param>
        /// <param name="downloadShare">Fraction of the phase spent downloading packages (0..1)</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// A single APT invocation counts from 0 to 100% twice, once while it downloads packages and
        /// once while dpkg installs them, so both stages need their own share of the phase
        /// </remarks>
        public async Task BeginPhaseAsync(string message, float start, float end, float downloadShare)
        {
            _phaseStart = start;
            _phaseEnd = end;
            _phaseSplit = start + (end - start) * downloadShare;
            _message = message;
            _progress = start;
            _lastReport = Environment.TickCount64;
            await connection.SetUpdateStatusAsync(message, start, Program.CancellationToken);
        }

        /// <summary>
        /// Report the progress of the current phase
        /// </summary>
        /// <param name="installing">Whether packages are being installed rather than downloaded</param>
        /// <param name="message">Description of the current step or null to keep the previous one</param>
        /// <param name="stageProgress">Progress within the current stage (0..1) or null if indeterminate</param>
        /// <returns>Asynchronous task</returns>
        public async Task ReportAsync(bool installing, string? message, float? stageProgress)
        {
            float progress = _progress;
            if (stageProgress is not null)
            {
                float stageStart = installing ? _phaseSplit : _phaseStart, stageEnd = installing ? _phaseEnd : _phaseSplit;

                // The overall progress must never decrease, else the bar in DWC jumps back and forth.
                // unattended-upgrade in particular reports bogus percentages while it is computing minimal upgrade steps
                progress = Math.Max(_progress, stageStart + (stageEnd - stageStart) * Math.Clamp(stageProgress.Value, 0f, 1f));
            }

            bool messageChanged = !string.IsNullOrEmpty(message) && message != _message;
            if (!messageChanged && (Environment.TickCount64 - _lastReport < MinReportInterval || progress - _progress < MinProgressDelta))
            {
                return;
            }

            if (messageChanged)
            {
                _message = message!;
            }
            _progress = progress;
            _lastReport = Environment.TickCount64;
            await connection.SetUpdateStatusAsync(_message, progress, Program.CancellationToken);
        }

        /// <summary>
        /// Command lines that indicate a package operation is still in progress
        /// </summary>
        /// <remarks>
        /// dpkg is matched with a trailing space so that dpkg-query, which other maintainer scripts
        /// invoke all the time, is not mistaken for an ongoing installation
        /// </remarks>
        private static readonly string[] UpgradeCommands = ["/usr/bin/unattended-upgrade", "install-dsf.sh", "/usr/bin/dpkg "];

        /// <summary>
        /// Check if a package operation is currently running
        /// </summary>
        /// <returns>Whether an upgrade is in progress</returns>
        private static bool IsUpgradeRunning()
        {
            foreach (string directory in Directory.GetDirectories("/proc"))
            {
                try
                {
                    string commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ');
                    if (UpgradeCommands.Any(commandLine.Contains))
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Process exited while its command line was being read
                }
            }
            return false;
        }

        /// <summary>
        /// Report that an upgrade is in progress without knowing how far along it is
        /// </summary>
        /// <param name="message">Description of the current step</param>
        /// <returns>Asynchronous task</returns>
        public async Task ReportIndeterminateAsync(string message)
        {
            _phaseStart = _phaseSplit = 0f;
            _phaseEnd = 1f;
            _message = message;
            _progress = 0f;
            _lastReport = Environment.TickCount64;
            await connection.SetUpdateStatusAsync(message, null, Program.CancellationToken);
        }

        /// <summary>
        /// Resume reporting the progress of an upgrade that outlived the last instance of this plugin
        /// </summary>
        /// <param name="socketPath">Path to the DCS socket</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// An upgrade that installs this plugin stops it halfway through, yet the unattended-upgrade process
        /// it started keeps running because it is reparented to init. DCS is restarted by the same upgrade and
        /// forgets that one is in progress, so the remaining progress would go unreported without this
        /// </remarks>
        public static async Task ResumeAsync(string socketPath)
        {
            try
            {
                if (!IsUpgradeRunning())
                {
                    return;
                }

                using CommandConnection connection = new();
                await connection.ConnectAsync(socketPath, Program.CancellationToken);

                // Only unattended-upgrade leaves a trail to derive a percentage from, so start out indeterminate
                UpgradeReporter reporter = new(connection);
                await reporter.ReportIndeterminateAsync("Finishing pending update");
                await reporter.WatchUnattendedUpgradeAsync(IsUpgradeRunning);
                await connection.SetUpdateStatusAsync(false, Program.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Plugin is shutting down again
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        /// <summary>
        /// Delete the progress file of a past upgrade so it cannot be mistaken for the current one
        /// </summary>
        /// <remarks>
        /// unattended-upgrade never deletes this file when it is done
        /// </remarks>
        public static void DeleteUnattendedUpgradeProgress()
        {
            try
            {
                File.Delete(UnattendedUpgradeProgressFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // File is going to be overwritten by unattended-upgrade anyway
            }
        }

        /// <summary>
        /// Report the progress of a running unattended-upgrade process until it exits
        /// </summary>
        /// <param name="isRunning">Check whether the process is still running</param>
        /// <returns>Asynchronous task</returns>
        /// <remarks>
        /// unattended-upgrade does not write machine-readable progress to stdout but it keeps
        /// a single-line status file up-to-date while it is installing packages
        /// </remarks>
        public async Task WatchUnattendedUpgradeAsync(Func<bool> isRunning)
        {
            while (isRunning())
            {
                await Task.Delay(ProgressFilePollInterval, Program.CancellationToken);

                string? content;
                try
                {
                    content = File.Exists(UnattendedUpgradeProgressFile) ? await File.ReadAllTextAsync(UnattendedUpgradeProgressFile, Program.CancellationToken) : null;
                }
                catch (IOException)
                {
                    // File is currently being rewritten, try again later
                    continue;
                }

                if (content is not null)
                {
                    Match match = _unattendedUpgradeProgressRegex().Match(content);
                    if (match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float percentage))
                    {
                        // apt separates multiple packages by bare commas, which leaves clients with one
                        // long unbreakable word to wrap
                        string package = string.Join(", ", match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        await ReportAsync(true, string.IsNullOrEmpty(package) ? null : $"Upgrading {package}", percentage / 100f);
                    }
                }
            }
        }
    }
}
