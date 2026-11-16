using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about five-bar parallel SCARA kinematics
/// </summary>
/// <remarks>
/// RepRapFirmware reports only the name of this geometry. These are the parameters M669 configures, so that the machine
/// can be recreated from the object model. Each pair is the left arm's value followed by the right arm's
/// </remarks>
public partial class FiveBarScaraKinematics : ZLeadscrewKinematics
{
    /// <summary>
    /// Minimum and maximum angles of the left actuator, then of the right actuator (in degrees, M669 C)
    /// </summary>
    public ObservableCollection<float> ActuatorAngleLimits { get; } = [0F, 0F, 0F, 0F];

    /// <summary>
    /// Lengths of the left and right cantilevered arms (in mm, the third and fourth values of M669 D)
    /// </summary>
    public ObservableCollection<float> CantileverLengths { get; } = [0F, 0F];

    /// <summary>
    /// Lengths of the left and right distal arms (in mm, M669 D)
    /// </summary>
    public ObservableCollection<float> DistalLengths { get; } = [0F, 0F];

    /// <summary>
    /// Minimum and maximum angles between the two distal arms (in degrees, the first two values of M669 A)
    /// </summary>
    public ObservableCollection<float> HeadAngleLimits { get; } = [0F, 0F];

    /// <summary>
    /// Angles of the left and right actuators when the homing switches trigger (in degrees, M669 B)
    /// </summary>
    public ObservableCollection<float> HomingAngles { get; } = [0F, 0F];

    /// <summary>
    /// Printable rectangle as the X and Y coordinates of one corner followed by those of the opposite corner (in mm, M669 Z),
    /// or empty if none is defined
    /// </summary>
    public ObservableCollection<float> PrintArea { get; } = [];

    /// <summary>
    /// Minimum and maximum angles between the left proximal and distal arms, then between the right ones (in degrees, the last four values of M669 A)
    /// </summary>
    public ObservableCollection<float> ProximalDistalAngleLimits { get; } = [0F, 0F, 0F, 0F];

    /// <summary>
    /// Lengths of the left and right proximal arms (in mm, M669 P)
    /// </summary>
    public ObservableCollection<float> ProximalLengths { get; } = [0F, 0F];

    /// <summary>
    /// Working mode, 1, 2 or 4 (M669 L)
    /// </summary>
    public int WorkMode
    {
        get => _workMode;
        set => SetPropertyValue(ref _workMode, value);
    }
    private int _workMode = 1;

    /// <summary>
    /// X coordinates of the left and right actuators (in mm, M669 X)
    /// </summary>
    public ObservableCollection<float> XOrigins { get; } = [0F, 0F];

    /// <summary>
    /// Y coordinates of the left and right actuators (in mm, M669 Y)
    /// </summary>
    public ObservableCollection<float> YOrigins { get; } = [0F, 0F];
}
