using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetPluginService.Permissions
{
    /// <summary>
    /// Permission enforcement using AppArmor
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
        /// <returns>Asynchronous task</returns>
        public static async Task InstallProfile(Plugin plugin)
        {
            // Load template
            string profile = await File.ReadAllTextAsync(Settings.AppArmorTemplate);
            profile = profile.Replace("{pluginDirectory}", Path.Combine(Settings.PluginDirectory, plugin.Id));

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
                            rules.AppendLine($"  owner {Settings.PluginDirectory.TrimEnd(Path.DirectorySeparatorChar)}/ r,");
                            rules.AppendLine($"  owner {Settings.PluginDirectory.TrimEnd(Path.DirectorySeparatorChar)}/** rw,");
                            // partially enforced by DCS
                            break;

                        case SbcPermissions.FileSystemAccess:
                            rules.AppendLine( "  / rw,");
                            rules.AppendLine( "  /** rw,");
                            break;
                        case SbcPermissions.LaunchProcesses:
                            rules.AppendLine("  /** mix,");
                            break;
                        case SbcPermissions.NetworkAccess:
                            includes.AppendLine("  #include <abstractions/nameservice>");
                            rules.AppendLine("  network,");
                            break;
                        case SbcPermissions.ReadFilaments:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "filaments")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "filaments")}/** r,");
                            break;
                        case SbcPermissions.WriteFilaments:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "filaments")}/** wk,");
                            break;
                        case SbcPermissions.ReadFirmware:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "firmware")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "firmware")}/** r,");
                            break;
                        case SbcPermissions.WriteFirmware:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "firmware")}/** wk,");
                            break;
                        case SbcPermissions.ReadGCodes:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "gcodes")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "gcodes")}/** r,");
                            break;
                        case SbcPermissions.WriteGCodes:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "gcodes")}/** wk,");
                            break;
                        case SbcPermissions.ReadMacros:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "macros")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "macros")}/** r,");
                            break;
                        case SbcPermissions.WriteMacros:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "macros")}/** wk,");
                            break;
                        case SbcPermissions.ReadMenu:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "menu")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "menu")}/** r,");
                            break;
                        case SbcPermissions.WriteMenu:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "menu")}/** wk,");
                            break;
                        case SbcPermissions.ReadSystem:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "sys")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "sys")}/** r,");
                            break;
                        case SbcPermissions.WriteSystem:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "sys")}/** wk,");
                            break;
                        case SbcPermissions.ReadWeb:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "www")}/ r,");
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "www")}/** r,");
                            break;
                        case SbcPermissions.WriteWeb:
                            rules.AppendLine($"  owner {Path.Combine(Settings.BaseDirectory, "www")}/** wk,");
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

            // Save and apply it
            string profilePath = Path.Combine(Settings.AppArmorProfileDirectory, $"dsf.{plugin.Id}");
            await File.WriteAllTextAsync(profilePath, profile);

            await System.Diagnostics.Process
                .Start(Settings.AppArmorParser, $"-r \"{profilePath}\"")
                .WaitForExitAsync(Program.CancellationToken);
        }

        /// <summary>
        /// Remove an AppArmor security profile for the given pugin and unload it
        /// </summary>
        /// <param name="plugin">Plugin</param>
        /// <returns>Asynchronous task</returns>
        public static async Task UninstallProfile(Plugin plugin)
        {
            string profilePath = Path.Combine(Settings.AppArmorProfileDirectory, $"dsf.{plugin.Id}");
            if (File.Exists(profilePath))
            {
                // Disable the profile via AppArmor
                await System.Diagnostics.Process
                    .Start(Settings.AppArmorParser, $"-R \"{profilePath}\"")
                    .WaitForExitAsync(Program.CancellationToken);

                // Delete it
                File.Delete(profilePath);
            }
        }
    }
}
