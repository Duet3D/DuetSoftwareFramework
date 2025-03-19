using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Uninstall a system package
/// </summary>
[RequiredPermissions(SbcPermissions.SuperUser)]
public partial class UninstallSystemPackage : Command
{
    /// <summary>
    /// Identifier of the package
    /// </summary>
    public string Package { get; set; } = string.Empty;
}
