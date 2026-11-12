using System;
using System.IO;

namespace DuetEmulation;

/// <summary>
/// Where the bench finds what it runs: the repository's firmware builds and the Renode models and
/// platforms in this project's source tree
/// </summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root: <c>$DUET_REPOSITORY_ROOT</c>, or the directory above this assembly holding CMakePresets.json</summary>
    public static string Root => RootPath.Value;

    /// <summary>Where the firmware presets build into, one directory per preset</summary>
    public static string Build => Path.Combine(Root, "build");

    /// <summary>Where the bench keeps what it generates and each run's files</summary>
    public static string Emulation => Path.Combine(Build, "emulation");

    /// <summary>The Renode peripheral models and platform descriptions</summary>
    public static string Renode => Path.Combine(Root, "src", "DuetEmulation", "Renode");

    /// <summary>Where SVD files the device packs in lib/CoreN2G lack are installed (.devcontainer/install-renode.sh)</summary>
    public static string InstalledSvds => Environment.GetEnvironmentVariable("DUET_EMULATION_SVD_DIR") is { Length: > 0 } directory
        ? directory
        : "/opt/renode/svd";

    private static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable("DUET_REPOSITORY_ROOT") is { Length: > 0 } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "DuetEmulation")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Cannot find the repository root above the test assembly; set $DUET_REPOSITORY_ROOT");
    }
}
