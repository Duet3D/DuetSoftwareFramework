using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Override the current status as reported by the object model when performing a software update
/// </summary>
[RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
public partial class SetUpdateStatus : Command
{
    /// <summary>
    /// Whether an update is now in progress
    /// </summary>
    public bool Updating { get; set; }

    /// <summary>
    /// Description of the current update step, only used if <see cref="Updating"/> is true
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Progress of the current update step (0..1) or null if indeterminate, only used if <see cref="Updating"/> is true
    /// </summary>
    public float? Progress { get; set; }
}
