using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace DuetPluginService.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.UninstallSystemPackage"/> command
/// </summary>
public sealed class UninstallSystemPackage(IOptions<Settings> settings) : DuetAPI.Commands.UninstallSystemPackage
{
    private readonly Settings _settings = settings.Value;

    /// <summary>
    /// Uninstall a system package
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="ArgumentException">Failed to uninstall package</exception>
    public override async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            throw new ArgumentException("Unable to manage system packages without root privileges");
        }

        string args = _settings.UninstallLocalPackageArguments.Replace("{package}", Package);
        using Process process = Process.Start(_settings.UninstallLocalPackageCommand, args);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new ArgumentException($"Failed to uninstall system package (exit code {process.ExitCode})");
        }
    }
}
