
using System.Reflection;

namespace DuetSharedLibrary;

/// <summary>
/// Version helper functions
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Get the version of the executing assembly
    /// </summary>
    public static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
