using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetPluginService.Permissions;

/// <summary>
/// Collection of functions to manage AppArmor permission enforcement
/// </summary>
/// <remarks>
/// This implementation still relies on fixed SD paths. In the future this code must react to changes of directories in the OM!
/// </remarks>
public static class AppArmor
{
    /// <summary>
    /// Generate an AppArmor security profile for the given plugin and load it
    /// </summary>
    /// <param name="plugin">Plugin</param>
    /// <param name="pluginDirectory">Plugin base directory</param>
    /// <param name="settings">Application settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async Task InstallProfileAsync(Plugin plugin, string pluginDirectory, string sdDirectory, Settings settings, CancellationToken cancellationToken)
    {
        // Load template
        string profile = await File.ReadAllTextAsync(settings.AppArmorTemplate, cancellationToken);
        profile = profile.Replace("{pluginDirectory}", Path.Combine(pluginDirectory, plugin.Id));

        // Build security profile
        StringBuilder includes = new(), rules = new();
        foreach (SbcPermissions permission in Enum.GetValues(typeof(SbcPermissions)))
        {
            if (plugin.SbcPermissions.HasFlag(permission))
            {
                switch (permission)
                {
                    case SbcPermissions.CodeInterceptionRead:
                    case SbcPermissions.CodeInterceptionReadWrite:
                    case SbcPermissions.CommandExecution:
                    case SbcPermissions.ManageUserSessions:
                    case SbcPermissions.ObjectModelRead:
                    case SbcPermissions.ObjectModelReadWrite:
                    case SbcPermissions.RegisterHttpEndpoints:
                    case SbcPermissions.ServicePlugins:
                        // enforced by DCS
                        break;

                    case SbcPermissions.ManagePlugins:
                        rules.AppendLine($"  {pluginDirectory.TrimEnd(Path.DirectorySeparatorChar)}/ r,");
                        rules.AppendLine($"  {pluginDirectory.TrimEnd(Path.DirectorySeparatorChar)}/** rw,");
                        // partially enforced by DCS
                        break;

                    case SbcPermissions.FileSystemAccess:
                        rules.AppendLine( "  / rw,");
                        rules.AppendLine( "  /** rw,");
                        break;
                    case SbcPermissions.GpioAccess:
                        rules.AppendLine("  /dev/gpio* rwmlk,");
                        rules.AppendLine("  /dev/i2c* rwmlk,");
                        rules.AppendLine("  /dev/spidev* rwmlk,");
                        break;
                    case SbcPermissions.LaunchProcesses:
                        rules.AppendLine("  /** mix,");
                        break;
                    case SbcPermissions.NetworkAccess:
                        includes.AppendLine("  #include <abstractions/nameservice>");
                        rules.AppendLine("  network,");
                        rules.AppendLine("  /proc/net/dev r,");
                        rules.AppendLine("  /proc/net/wireless r,");
                        break;
                    case SbcPermissions.WebcamAccess:
                        rules.AppendLine("  /dev/dma_heap/* rw,");
                        rules.AppendLine("  /dev/media* rwmlk,");
                        rules.AppendLine("  /dev/v4l-* rwmlk,");
                        rules.AppendLine("  /dev/video* rwmlk,");
                        rules.AppendLine("  /run/udev/data/** rwmlk,");
                        rules.AppendLine("  /usr/bin/libcamerify rm,");
                        rules.AppendLine("  /usr/libexec/libcamera/* rm,");
                        rules.AppendLine("  /usr/share/libcamera/** r,");
                        break;
                    case SbcPermissions.ReadFilaments:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "filaments")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "filaments")}/** r,");
                        break;
                    case SbcPermissions.WriteFilaments:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "filaments")}/** wk,");
                        break;
                    case SbcPermissions.ReadFirmware:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "firmware")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "firmware")}/** r,");
                        break;
                    case SbcPermissions.WriteFirmware:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "firmware")}/** wk,");
                        break;
                    case SbcPermissions.ReadGCodes:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "gcodes")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "gcodes")}/** r,");
                        break;
                    case SbcPermissions.WriteGCodes:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "gcodes")}/** wk,");
                        break;
                    case SbcPermissions.ReadMacros:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "macros")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "macros")}/** r,");
                        break;
                    case SbcPermissions.WriteMacros:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "macros")}/** wk,");
                        break;
                    case SbcPermissions.ReadMenu:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "menu")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "menu")}/** r,");
                        break;
                    case SbcPermissions.WriteMenu:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "menu")}/** wk,");
                        break;
                    case SbcPermissions.ReadSystem:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "sys")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "sys")}/** r,");
                        break;
                    case SbcPermissions.WriteSystem:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "sys")}/** wk,");
                        break;
                    case SbcPermissions.ReadWeb:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "www")}/ r,");
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "www")}/** r,");
                        break;
                    case SbcPermissions.WriteWeb:
                        rules.AppendLine($"  {Path.Combine(sdDirectory, "www")}/** wk,");
                        break;

                    case SbcPermissions.None:
                    case SbcPermissions.SuperUser:
                        // not applicable
                        break;
                }
            }

        }
        profile = profile.Replace("{includes}", includes.ToString());
        profile = profile.Replace("{rules}", rules.ToString());

        // Save and apply it. This must not be interrupted!
        string profilePath = Path.Combine(settings.AppArmorProfileDirectory, $"dsf.{plugin.Id}");
        await File.WriteAllTextAsync(profilePath, profile, CancellationToken.None);

        // Load new profile
        await System.Diagnostics.Process
            .Start(settings.AppArmorParser, $"-r \"{profilePath}\"")
            .WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Remove an AppArmor security profile for the given pugin and unload it
    /// </summary>
    /// <param name="pluginId">Plugin ID</param>
    /// <param name="settings">Application settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public static async Task UninstallProfileAsync(string pluginId, Settings settings, CancellationToken cancellationToken)
    {
        string profilePath = Path.Combine(settings.AppArmorProfileDirectory, $"dsf.{pluginId}");
        if (File.Exists(profilePath))
        {
            // Disable the profile via AppArmor
            await System.Diagnostics.Process
                .Start(settings.AppArmorParser, $"-R \"{profilePath}\"")
                .WaitForExitAsync(cancellationToken);

            // Delete it
            File.Delete(profilePath);
        }
    }
}
