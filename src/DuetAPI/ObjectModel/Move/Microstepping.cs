namespace DuetAPI.ObjectModel;

/// <summary>
/// Microstepping configuration
/// </summary>
public partial class Microstepping : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Indicates if the stepper driver uses interpolation
    /// </summary>
    /// <remarks>
    /// A drive starts at x16 with interpolation, which is what RepRapFirmware sets every drive to
    /// before config.g runs (Move.cpp Move::Init, <c>SetDriverMicrostepping(drive, 16, true)</c>).
    /// Every Duet 3 driver interpolates at every microstep setting, so this is the state a machine
    /// that never mentions M350 runs in, and the one a bare M350 reports
    /// </remarks>
    public bool Interpolated
    {
        get => _interpolated;
        set => SetPropertyValue(ref _interpolated, value);
    }
    private bool _interpolated = true;

    /// <summary>
    /// Microsteps per full step
    /// </summary>
    public int Value
    {
        get => _value;
        set => SetPropertyValue(ref _value, value);
    }
    private int _value = 16;
}
