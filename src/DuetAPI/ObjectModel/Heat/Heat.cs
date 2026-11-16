using System;
using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the heat subsystem
/// </summary>
public partial class Heat : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Default temperature used to set the cold extrusion threshold temperature
    /// </summary>
    public const float DefaultColdExtrusionTemperature = 160F;

    /// <summary>
    /// Default temperature used to set the cold retract threshold temperature
    /// </summary>
    public const float DefaultColdRetractTemperature = 90F;

    /// <summary>
    /// List of configured bed heaters (indices)
    /// </summary>
    /// <seealso cref="Heater"/>
    /// <remarks>
    /// Items may be -1 if unconfigured
    /// </remarks>
    [Obsolete("use bedHeaterMapping instead")]
    public ObservableCollection<int> BedHeaters { get; } = [];

    /// <summary>
    /// List of configured bed heaters (indices), grouped by bed (first dimension)
    /// </summary>
    /// <remarks>
    /// Items may be -1 if unconfigured
    /// </remarks>
    public ObservableCollection<int[]> BedHeaterMapping { get; } = [];

    /// <summary>
    /// List of configured chamber heaters (indices)
    /// </summary>
    /// <seealso cref="Heater"/>
    /// <remarks>
    /// Items may be -1 if unconfigured
    /// </remarks>
    [Obsolete("use chamberHeaterMapping instead")]
    public ObservableCollection<int> ChamberHeaters { get; } = [];
    
    /// <summary>
    /// List of configured chamber heaters (indices), grouped by chamber (first dimension)
    /// </summary>
    /// <remarks>
    /// Items may be -1 if unconfigured
    /// </remarks>
    public ObservableCollection<int[]> ChamberHeaterMapping { get; } = [];
    
    /// <summary>
    /// Whether cold extrusion is allowed (M302 P)
    /// </summary>
    /// <remarks>
    /// While it is, <see cref="ColdExtrudeTemperature"/> and <see cref="ColdRetractTemperature"/> report 0 and the
    /// configured minimums are kept in <see cref="MinExtrusionTemperature"/> and <see cref="MinRetractionTemperature"/>
    /// </remarks>
    public bool ColdExtrusionAllowed
    {
        get => _coldExtrusionAllowed;
        set => SetPropertyValue(ref _coldExtrusionAllowed, value);
    }
    private bool _coldExtrusionAllowed;

    /// <summary>
    /// Minimum required temperature for extrusion moves (in C), or 0 while cold extrusion is allowed
    /// </summary>
    public float ColdExtrudeTemperature
    {
        get => _coldExtrudeTemperature;
        set => SetPropertyValue(ref _coldExtrudeTemperature, value);
    }
    private float _coldExtrudeTemperature = DefaultColdExtrusionTemperature;

    /// <summary>
    /// Minimum required temperature for retraction moves (in C), or 0 while cold extrusion is allowed
    /// </summary>
    public float ColdRetractTemperature
    {
        get => _coldRetractTemperature;
        set => SetPropertyValue(ref _coldRetractTemperature, value);
    }
    private float _coldRetractTemperature = DefaultColdRetractTemperature;

    /// <summary>
    /// Configured minimum temperature for extrusion moves (M302 S, in C), kept while cold extrusion is allowed
    /// </summary>
    public float MinExtrusionTemperature
    {
        get => _minExtrusionTemperature;
        set => SetPropertyValue(ref _minExtrusionTemperature, value);
    }
    private float _minExtrusionTemperature = DefaultColdExtrusionTemperature;

    /// <summary>
    /// Configured minimum temperature for retraction moves (M302 R, in C), kept while cold extrusion is allowed
    /// </summary>
    public float MinRetractionTemperature
    {
        get => _minRetractionTemperature;
        set => SetPropertyValue(ref _minRetractionTemperature, value);
    }
    private float _minRetractionTemperature = DefaultColdRetractTemperature;

    /// <summary>
    /// List of configured heaters
    /// </summary>
    /// <seealso cref="Heater"/>
    [Live]
    public StaticModelCollection<Heater?> Heaters { get; } = [];
}
