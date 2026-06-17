namespace DuetAPI.ObjectModel;

/// <summary>
/// Move segmentation parameters
/// </summary>
public partial class MoveSegmentation : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Number of segments per second
    /// </summary>
    public float SegmentsPerSec
    {
        get => _segmentsPerSec;
        set => SetPropertyValue(ref _segmentsPerSec, value);
    }
    private float _segmentsPerSec;

    /// <summary>
    /// Minimum length of a segment (in mm)
    /// </summary>
    public float MinSegLength
    {
        get => _minSegLength;
        set => SetPropertyValue(ref _minSegLength, value);
    }
    private float _minSegLength;
}
