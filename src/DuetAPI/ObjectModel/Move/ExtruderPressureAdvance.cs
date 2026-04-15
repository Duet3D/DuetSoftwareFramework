namespace DuetAPI.ObjectModel;

/// <summary>
/// Pressure advance parameters (see M572)
/// </summary>
public partial class ExtruderPressureAdvance : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Delay coefficient (in ms), or null if pressure advance is in simple mode (k0 = 0) - RRF reports infinity here as null
    /// </summary>
    public float? D
    {
        get => _d;
        set => SetPropertyValue(ref _d, value);
    }
    private float? _d;

    /// <summary>
    /// K0 coefficient
    /// </summary>
    public float K0
    {
        get => _k0;
        set => SetPropertyValue(ref _k0, value);
    }
    private float _k0;

    /// <summary>
    /// K1 coefficient
    /// </summary>
    public float K1
    {
        get => _k1;
        set => SetPropertyValue(ref _k1, value);
    }
    private float _k1;
}
