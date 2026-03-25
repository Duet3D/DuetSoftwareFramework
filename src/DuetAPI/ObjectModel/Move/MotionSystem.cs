using System;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a motion system
/// </summary>
public partial class MotionSystem : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Information about the current move
    /// </summary>
    public CurrentMove CurrentMove { get; } = new CurrentMove();

    /// <summary>
    /// Number of the current object being printed or null if not printing
    /// </summary>
    public int? CurrentObject
    {
        get => _currentObject;
        set => SetPropertyValue(ref _currentObject, value);
    }
    private int? _currentObject;

    /// <summary>
    /// Number of the currently selected tool or -1 if none is selected
    /// </summary>
    public int CurrentTool
    {
        get => _currentTool;
        set => SetPropertyValue(ref _currentTool, value);
    }
    private int _currentTool = -1;

    /// <summary>
    /// Number of the next tool to be selected
    /// </summary>
    public int NextTool
    {
        get => _nextTool;
        set => SetPropertyValue(ref _nextTool, value);
    }
    private int _nextTool = -1;

    /// <summary>
    /// Number of the previous tool
    /// </summary>
    public int PreviousTool
    {
        get => _previousTool;
        set => SetPropertyValue(ref _previousTool, value);
    }
    private int _previousTool = -1;

    /// <summary>
    /// Maximum acceleration allowed while printing (in mm/s^2)
    /// </summary>
    public float PrintingAcceleration
    {
        get => _printingAcceleration;
        set => SetPropertyValue(ref _printingAcceleration, value);
    }
    private float _printingAcceleration = 10000F;

    /// <summary>
    /// List of restore points
    /// </summary>
    public StaticModelCollection<RestorePoint> RestorePoints { get; } = [];

    /// <summary>
    /// Parameters for centre rotation
    /// </summary>
    public MoveRotation Rotation { get; } = new MoveRotation();

    /// <summary>
    /// Speed factor applied to every regular move (0.01..1 or greater)
    /// </summary>
    public float SpeedFactor
    {
        get => _speedFactor;
        set => SetPropertyValue(ref _speedFactor, value);
    }
    private float _speedFactor = 1F;

    /// <summary>
    /// Maximum acceleration allowed while travelling (in mm/s^2)
    /// </summary>
    public float TravelAcceleration
    {
        get => _travelAcceleration;
        set => SetPropertyValue(ref _travelAcceleration, value);
    }
    private float _travelAcceleration = 10000F;

    /// <summary>
    /// User coordinates of the motion system
    /// </summary>
    public ObservableCollection<float> UserPosition { get; } = [];

    /// <summary>
    /// Virtual total extruder position
    /// </summary>
    public float VirtualEPos
    {
        get => _virtualEPos;
        set => SetPropertyValue(ref _virtualEPos, value);
    }
    private float _virtualEPos;

    /// <summary>
    /// Index of the currently selected workplace
    /// </summary>
    public int WorkplaceNumber
    {
        get => _workplaceNumber;
        set => SetPropertyValue(ref _workplaceNumber, value);
    }
    private int _workplaceNumber;
}
