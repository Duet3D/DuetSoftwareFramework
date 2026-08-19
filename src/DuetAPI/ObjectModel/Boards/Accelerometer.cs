namespace DuetAPI.ObjectModel;

/// <summary>
/// This represents an accelerometer
/// </summary>
public partial class Accelerometer : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Orientation of the accelerometer
    /// </summary>
    /// <remarks>
    /// See https://docs.duet3d.com/en/Duet3D_hardware/Accessories/Duet3D_Accelerometer#orientation for a list of orientations
    /// </remarks>
    public int Orientation
    {
        get => _orientation;
        set => SetPropertyValue(ref _orientation, value);
    }
    private int _orientation = 20;

    /// <summary>
    /// Number of collected data points in the last run or 0 if it failed
    /// </summary>
    public int Points
    {
        get => _points;
        set => SetPropertyValue(ref _points, value);
    }
    private int _points;

    /// <summary>
    /// Resolution the accelerometer is programmed for (in bits) or 0 if unknown
    /// </summary>
    public int Resolution
    {
        get => _resolution;
        set => SetPropertyValue(ref _resolution, value);
    }
    private int _resolution;

    /// <summary>
    /// Number of completed sampling runs
    /// </summary>
    public int Runs
    {
        get => _runs;
        set => SetPropertyValue(ref _runs, value);
    }
    private int _runs;

    /// <summary>
    /// Rate the accelerometer is programmed for (in Hz) or 0 if unknown
    /// </summary>
    /// <remarks>
    /// This is the rate the accelerometer settled on, which may be lower than the one M955 asked for.
    /// Once it has completed a run, the rate measured during that run is reported instead
    /// </remarks>
    public int SamplingRate
    {
        get => _samplingRate;
        set => SetPropertyValue(ref _samplingRate, value);
    }
    private int _samplingRate;
}
