namespace DuetAPI.ObjectModel;

/// <summary>
/// Details about a software upgrade in progress
/// </summary>
public partial class Upgrade : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Description of the current upgrade step
    /// </summary>
    public string Message
    {
        get => _message;
        set => SetPropertyValue(ref _message, value);
    }
    private string _message = string.Empty;

    /// <summary>
    /// Progress of the current upgrade step (0..1) or null if indeterminate
    /// </summary>
    public float? Progress
    {
        get => _progress;
        set => SetPropertyValue(ref _progress, value);
    }
    private float? _progress;
}
