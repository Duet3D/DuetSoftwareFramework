namespace DuetAPI.ObjectModel;

/// <summary>
/// One harmonic of the sine table waveform correction of a driver (M569.2 S, J and O)
/// </summary>
public partial class DriverWaveformCorrection : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Harmonic of the electrical cycle being corrected (M569.2 S)
    /// </summary>
    public int Harmonic
    {
        get => _harmonic;
        set => SetPropertyValue(ref _harmonic, value);
    }
    private int _harmonic;

    /// <summary>
    /// Correction magnitude in degrees (M569.2 J)
    /// </summary>
    public float Magnitude
    {
        get => _magnitude;
        set => SetPropertyValue(ref _magnitude, value);
    }
    private float _magnitude;

    /// <summary>
    /// Correction phase in degrees, 0 or 180 (M569.2 O)
    /// </summary>
    public float Phase
    {
        get => _phase;
        set => SetPropertyValue(ref _phase, value);
    }
    private float _phase;
}
