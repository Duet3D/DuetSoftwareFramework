using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a load cell probe
/// </summary>
public partial class ProbeLoadCell : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Force measured by the load cell relative to the last tare (in g)
    /// </summary>
    [Live]
    public float Force
    {
        get => _force;
        set => SetPropertyValue(ref _force, value);
    }
    private float _force;

    /// <summary>
    /// Scale of the load cell (in g per count)
    /// </summary>
    public float GramsPerCount
    {
        get => _gramsPerCount;
        set => SetPropertyValue(ref _gramsPerCount, value);
    }
    private float _gramsPerCount;

    /// <summary>
    /// Preload of the load cell at the last tare (in g)
    /// </summary>
    public float Preload
    {
        get => _preload;
        set => SetPropertyValue(ref _preload, value);
    }
    private float _preload;

    /// <summary>
    /// Safe window for the preload (in g, low and high limit). Two equal values disable the check
    /// </summary>
    public ObservableCollection<float> PreloadWindow { get; } = [0F, 0F];
}
