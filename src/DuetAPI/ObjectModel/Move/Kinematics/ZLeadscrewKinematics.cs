namespace DuetAPI.ObjectModel;

/// <summary>
/// Base kinematics class that provides the ability to level the bed using Z leadscrews
/// </summary>
public partial class ZLeadscrewKinematics : Kinematics
{
    /// <summary>
    /// Coordinates M671 may give for the leadscrews or levelling screws of one axis
    /// </summary>
    public const int MaxLeadscrews = 4;

    /// <summary>
    /// Parameters describing the tilt correction
    /// </summary>
    public TiltCorrection TiltCorrection { get; } = new TiltCorrection();
}
