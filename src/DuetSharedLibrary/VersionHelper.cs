using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DuetSharedLibrary;

/// <summary>
/// Helper class to retrieve the version of the application
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Gets the version of the application
    /// </summary>
    /// <returns>Application version</returns>
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Assembly.Location is empty in single-file and AOT builds, which the emptiness check below handles by falling through")]
    public static string GetVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        // 1) MSBuild populates this from <InformationalVersion> (or <Version>)
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            return info;
        }

        // 2) you can also grab the file-version resource. Assembly.Location is empty in single-file and AOT
        // builds, where the check below simply falls through to the assembly name
        if (!string.IsNullOrEmpty(asm.Location))
        {
            var fvi = FileVersionInfo.GetVersionInfo(asm.Location).ProductVersion;
            if (!string.IsNullOrEmpty(fvi))
            {
                return fvi;
            }
        }

        // 3) finally fall back to AssemblyName.Version
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}
