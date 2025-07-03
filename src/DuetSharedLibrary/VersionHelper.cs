using System.Diagnostics;
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
    public static string GetVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        // 1) MSBuild populates this from <InformationalVersion> (or <Version>)
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            return info;
        }

        // 2) you can also grab the file-version resource
        if (!string.IsNullOrEmpty(asm.Location))
        {
            var fvi = FileVersionInfo.GetVersionInfo(asm.Location).ProductVersion;
            if (!string.IsNullOrEmpty(fvi))
                return fvi;
        }

        // 3) finally fall back to AssemblyName.Version
        return asm.GetName().Version?.ToString() ?? "unknown";
    }
}