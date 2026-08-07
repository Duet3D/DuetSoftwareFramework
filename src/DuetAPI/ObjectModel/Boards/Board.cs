using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a connected board
/// </summary>
/// <remarks>
/// Every item of the boards array is either a <see cref="MainBoard"/> or an <see cref="ExpansionBoard"/>,
/// which is decided by its position, see <see cref="Boards"/>
/// </remarks>
[JsonDerivedType(typeof(MainBoard))]
[JsonDerivedType(typeof(ExpansionBoard))]
public partial class Board : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Accelerometer of this board or null if unknown
    /// </summary>
    public Accelerometer? Accelerometer
    {
        get => _accelerometer;
        set => SetPropertyValue(ref _accelerometer, value);
    }
    private Accelerometer? _accelerometer;

    /// <summary>
    /// CAN address of this board or null if not applicable
    /// </summary>
    public int? CanAddress
    {
        get => _canAddress;
        set => SetPropertyValue(ref _canAddress, value);
    }
    private int? _canAddress;

    /// <summary>
    /// Drivers of this board
    /// </summary>
    public StaticModelCollection<Driver>? Drivers
    {
        get => _drivers;
        set => SetPropertyValue(ref _drivers, value);
    }
    private StaticModelCollection<Driver>? _drivers;

    /// <summary>
    /// Date of the firmware build
    /// </summary>
    public string FirmwareDate
    {
        get => _firmwareDate;
        set => SetPropertyValue(ref _firmwareDate, value);
    }
    private string _firmwareDate = string.Empty;

    /// <summary>
    /// Filename of the firmware binary
    /// </summary>
    public string FirmwareFileName
    {
        get => _firmwareFileName;
        set => SetPropertyValue(ref _firmwareFileName, value);
    }
    private string _firmwareFileName = string.Empty;

    /// <summary>
    /// Version of the firmware build
    /// </summary>
    public string FirmwareVersion
    {
        get => _firmwareVersion;
        set => SetPropertyValue(ref _firmwareVersion, value);
    }
    private string _firmwareVersion = string.Empty;

    /// <summary>
    /// Amount of free RAM on this board (in bytes or null if unknown)
    /// </summary>
    public int? FreeRam
    {
        get => _freeRam;
        set => SetPropertyValue(ref _freeRam, value);
    }
    private int? _freeRam;

    /// <summary>
    /// Maximum number of motors this board can drive
    /// </summary>
    public int MaxMotors
    {
        get => _maxMotors;
        set => SetPropertyValue(ref _maxMotors, value);
    }
    private int _maxMotors;

    /// <summary>
    /// Minimum, maximum, and current temperatures of the MCU or null if unknown
    /// </summary>
    public MinMaxCurrent? McuTemp
    {
        get => _mcuTemp;
        set => SetPropertyValue(ref _mcuTemp, value);
    }
    private MinMaxCurrent? _mcuTemp;

    /// <summary>
    /// Full name of the board
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetPropertyValue(ref _name, value);
    }
    private string _name = string.Empty;

    /// <summary>
    /// Short name of this board
    /// </summary>
    public string ShortName
    {
        get => _shortName;
        set => SetPropertyValue(ref _shortName, value);
    }
    private string _shortName = string.Empty;

    /// <summary>
    /// Unique identifier of the board or null if unknown
    /// </summary>
    public string? UniqueId
    {
        get => _uniqueId;
        set => SetPropertyValue(ref _uniqueId, value);
    }
    private string? _uniqueId;

    /// <summary>
    /// Minimum, maximum, and current voltages on the 12V rail or null if unknown
    /// </summary>
    public MinMaxCurrent? V12
    {
        get => _v12;
        set => SetPropertyValue(ref _v12, value);
    }
    private MinMaxCurrent? _v12;

    /// <summary>
    /// Minimum, maximum, and current voltages on the input rail or null if unknown
    /// </summary>
    public MinMaxCurrent? VIn
    {
        get => _vIn;
        set => SetPropertyValue(ref _vIn, value);
    }
    private MinMaxCurrent? _vIn;
}
