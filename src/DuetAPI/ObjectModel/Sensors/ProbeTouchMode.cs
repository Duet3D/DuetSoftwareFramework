namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a configured probe
/// </summary>
public partial class ProbeTouchMode : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Indicates if the touch probe is enabled
    /// </summary>
    public bool Active
    {
        get => _active;
        set => SetPropertyValue(ref _active, value);
    }
    private bool _active;

    /// <summary>
    /// Speed while probing in touch mode (in mm/s)
    /// </summary>
    public float Speed
    {
        get => _speed;
        set => SetPropertyValue(ref _speed, value);
    }
    private float _speed;

    /// <summary>
    /// Threshold value of the touch probe
    /// </summary>
    public float Threshold
    {
        get => _threshold;
        set => SetPropertyValue(ref _threshold, value);
    }
    private float _threshold;

    /// <summary>
    /// Height of the trigger point of the touch probe (in mm)
    /// </summary>
    public float TriggerHeight
    {
        get => _triggerHeight;
        set => SetPropertyValue(ref _triggerHeight, value);
    }
    private float _triggerHeight;
}
