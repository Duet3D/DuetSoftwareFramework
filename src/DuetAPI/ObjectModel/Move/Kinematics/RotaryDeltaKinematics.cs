using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about rotary delta kinematics
/// </summary>
/// <remarks>
/// RepRapFirmware reports only the name of this geometry. These are the parameters M669 and M666 configure,
/// so that the machine can be recreated from the object model
/// </remarks>
public partial class RotaryDeltaKinematics : Kinematics
{
    /// <summary>
    /// Corrections to the angles of the X, Y and Z arms' inner joints (in degrees, M669 X, Y and Z)
    /// </summary>
    public ObservableCollection<float> AngleCorrections { get; } = [0F, 0F, 0F];

    /// <summary>
    /// Minimum and maximum arm angles above the horizontal (in degrees, M669 A). The maximum is also where the homing switches trigger
    /// </summary>
    public ObservableCollection<float> ArmAngleLimits { get; } = [-45F, 45F];

    /// <summary>
    /// Lengths of the X, Y and Z arms (in mm, M669 U)
    /// </summary>
    public ObservableCollection<float> ArmLengths { get; } = [100F, 100F, 100F];

    /// <summary>
    /// Heights of the X, Y and Z arms' bearings above the effector joints at Z=0 (in mm, M669 H)
    /// </summary>
    public ObservableCollection<float> BearingHeights { get; } = [250F, 250F, 250F];

    /// <summary>
    /// Delta radius (in mm, M669 R)
    /// </summary>
    public float DeltaRadius
    {
        get => _deltaRadius;
        set => SetPropertyValue(ref _deltaRadius, value);
    }
    private float _deltaRadius = 50F;

    /// <summary>
    /// Endstop adjustments of the X, Y and Z arms (in degrees, M666 X, Y and Z)
    /// </summary>
    public ObservableCollection<float> EndstopAdjustments { get; } = [0F, 0F, 0F];

    /// <summary>
    /// Printable radius (in mm, M669 B)
    /// </summary>
    public float PrintRadius
    {
        get => _printRadius;
        set => SetPropertyValue(ref _printRadius, value);
    }
    private float _printRadius = 80F;

    /// <summary>
    /// Lengths of the X, Y and Z rod pairs (in mm, M669 L)
    /// </summary>
    public ObservableCollection<float> RodLengths { get; } = [200F, 200F, 200F];
}
