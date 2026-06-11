using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Kinematics class for SCARA kinematics
/// </summary>
public partial class ScaraKinematics : ZLeadscrewKinematics
{
    /// <summary>
    /// Proximal to distal, proximal to Z and distal to Z crosstalk
    /// </summary>
    public ObservableCollection<float> Crosstalk { get; } = [0F, 0F, 0F];

    /// <summary>
    /// Distal arm length (in mm)
    /// </summary>
    public float DistalLength
    {
        get => _distalArmLength;
        set => SetPropertyValue(ref _distalArmLength, value);
    }
    private float _distalArmLength;

    /// <summary>
    /// Requested minimum radius (in mm)
    /// </summary>
    public float MinRadius
    {
        get => _minRadius;
        set => SetPropertyValue(ref _minRadius, value);
    }
    private float _minRadius;

    /// <summary>
    /// Proximal arm length (in mm)
    /// </summary>
    public float ProximalLength
    {
        get => _proximalArmLength;
        set => SetPropertyValue(ref _proximalArmLength, value);
    }
    private float _proximalArmLength;

    /// <summary>
    /// Psi limits (in degrees)
    /// </summary>
    public ObservableCollection<float> PsiLimits { get; } = [0F, 0F];

    /// <summary>
    /// Theta limits (in degrees)
    /// </summary>
    public ObservableCollection<float> ThetaLimits { get; } = [0F, 0F];

    /// <summary>
    /// X offset (in mm)
    /// </summary>
    public float XOffset
    {
        get => _xOffset;
        set => SetPropertyValue(ref _xOffset, value);
    }
    private float _xOffset;

    /// <summary>
    /// Y offset (in mm)
    /// </summary>
    public float YOffset
    {
        get => _yOffset;
        set => SetPropertyValue(ref _yOffset, value);
    }
    private float _yOffset;
}
