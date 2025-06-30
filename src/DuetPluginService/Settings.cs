using System.Collections.Generic;

namespace DuetPluginService;

/// <summary>
/// Settings class
/// </summary>
public class Settings
{
    /// <summary>
    /// Default path to the configuration file
    /// </summary>
    public const string DefaultConfigFile = "/opt/dsf/conf/plugins.json";

    /// <summary>
    /// Path to the UNIX socket provided by DuetControlServer
    /// </summary>
    public string SocketPath { get; set; } = DuetAPI.Connection.Defaults.FullSocketPath;

    /// <summary>
    /// Disable AppArmor security policy generation (not recommended, potential security hazard)
    /// </summary>
    public bool DisableAppArmor { get; set; }

    /// <summary>
    /// Path to the utility that allows profile management
    /// </summary>
    public string AppArmorParser { get; set; } = "/usr/sbin/apparmor_parser";

    /// <summary>
    /// Directory holding AppArmor security profiles
    /// </summary>
    public string AppArmorTemplate { get; set; } = "/opt/dsf/conf/apparmor.conf";

    /// <summary>
    /// Directory holding AppArmor security profiles
    /// </summary>
    public string AppArmorProfileDirectory { get; set; } = "/etc/apparmor.d";

    /// <summary>
    /// Command to run before installing third-party packages
    /// </summary>
    public string PreinstallPackageCommand { get; set; } = "/usr/bin/apt";

    /// <summary>
    /// Command-line arguments to use before installing third-party packages
    /// </summary>
    public string PreinstallPackageArguments { get; set; } = "update";

    /// <summary>
    /// Command to install third-party packages
    /// </summary>
    public string InstallPackageCommand { get; set; } = "/usr/bin/apt";

    /// <summary>
    /// Command-line arguments to install third-party packages
    /// </summary>
    public string InstallPackageArguments { get; set; } = "install -y {package}";

    /// <summary>
    /// Command to install third-party Python packages
    /// </summary>
    public string InstallPythonPackageCommand { get; set; } = "/opt/dsf/bin/pipInstall2.py";

    /// <summary>
    /// Command-line arguments to install third-party Python packages
    /// </summary>
    public string InstallPythonPackageArguments { get; set; } = "-m {manifestFile} -p {pluginPath}";

    /// <summary>
    /// Environment variables for the installation command
    /// </summary>
    public Dictionary<string, string> InstallPackageEnvironment { get; set; } = new Dictionary<string, string>()
        {
            { "DEBIAN_FRONTEND", "noninteractive"  }
        };

    /// <summary>
    /// Command to install a local package
    /// </summary>
    public string InstallLocalPackageCommand { get; set; } = "/usr/bin/dpkg";

    /// <summary>
    /// Command-line arguments to install a local package
    /// </summary>
    public string InstallLocalPackageArguments { get; set; } = "--force-confold -i {file}";

    /// <summary>
    /// Command to uninstall a local package
    /// </summary>
    public string UninstallLocalPackageCommand { get; set; } = "/usr/bin/dpkg";

    /// <summary>
    /// Command-line arguments to uninstall a local package
    /// </summary>
    public string UninstallLocalPackageArguments { get; set; } = "-r {package}";

    /// <summary>
    /// Command to launch Python plugin scripts that have a virtual environment
    /// </summary>
    public string PythonLaunchCommand { get; set; } = "/bin/bash";

    /// <summary>
    /// Command-line arguments to invoke Python scripts with virtual environments
    /// </summary>
    public string PythonLaunchArguments { get; set; } = "-c \"{pluginDir}/venv/bin/python {command} {args}\"";

    /// <summary>
    /// Timeout in ms for SIGTERM requests. When it expires plugin processes are forcefully killed
    /// </summary>
    public int StopTimeout { get; set; } = 4000;
}
