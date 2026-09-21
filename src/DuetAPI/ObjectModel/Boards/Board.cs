using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about a connected board
/// </summary>
public partial class Board : ModelObject, IStaticModelObject
{
    /// <summary>
    /// Connection timeout a board has until it is given another
    /// </summary>
    /// <remarks>RepRapFirmware's <c>DefaultConnectionTimeoutSeconds</c> (ExpansionManager.h)</remarks>
    public const int DefaultConnectionTimeoutSeconds = 10;

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
    /// Closed loop data of this board or null if unknown
    /// </summary>
    public BoardClosedLoop? ClosedLoop
    {
        get => _closedLoop;
        set => SetPropertyValue(ref _closedLoop, value);
    }
    private BoardClosedLoop? _closedLoop;

    /// <summary>
    /// Details about a connected display or null if none is connected
    /// </summary>
    public DirectDisplay? DirectDisplay
    {
        get => _directDisplay;
        set => SetPropertyValue(ref _directDisplay, value);
    }
    private DirectDisplay? _directDisplay;

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
    /// Name of the firmware build, or null if this board does not report one
    /// </summary>
    /// <remarks>
    /// Only the main board has one. RepRapFirmware reports this from <c>Platform</c>'s object model
    /// table, which serves <c>boards[0]</c> alone; an expansion board announces its type and version
    /// and never names its firmware, so <c>ExpansionManager</c>'s table has no such entry
    /// </remarks>
    public string? FirmwareName
    {
        get => _firmwareName;
        set => SetPropertyValue(ref _firmwareName, value);
    }
    private string? _firmwareName;

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
    /// Filename of the IAP binary that is used for updates from the SBC or null if unsupported
    /// </summary>
    /// <remarks>
    /// This is only available for the mainboard (first board item)
    /// </remarks>
    public string? IapFileNameSBC
    {
        get => _iapFileNameSBC;
        set => SetPropertyValue(ref _iapFileNameSBC, value);
    }
    private string? _iapFileNameSBC;

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
    /// Filename of the IAP binary that is used for updates from the SD card or null if unsupported
    /// </summary>
    /// <remarks>
    /// This is only available for the mainboard (first board item)
    /// </remarks>
    public string? IapFileNameSD
    {
        get => _iapFileNameSD;
        set => SetPropertyValue(ref _iapFileNameSD, value);
    }
    private string? _iapFileNameSD;

    /// <summary>
    /// Information about an inductive sensor or null if not present
    /// </summary>
    public InductiveSensor? InductiveSensor
    {
        get => _inductiveSensor;
        set => SetPropertyValue(ref _inductiveSensor, value);
    }
    private InductiveSensor? _inductiveSensor;

    /// <summary>
    /// Maximum number of heaters this board can control, or null if this board does not report one
    /// </summary>
    /// <remarks>
    /// Only the main board has one, for the same reason as <see cref="FirmwareName"/>: it comes from
    /// the object model table RepRapFirmware serves <c>boards[0]</c> from, and an expansion board
    /// reports how many drivers it carries but never how many heaters
    /// </remarks>
    public int? MaxHeaters
    {
        get => _maxHeaters;
        set => SetPropertyValue(ref _maxHeaters, value);
    }
    private int? _maxHeaters;

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
    /// State of this board
    /// </summary>
    /// <remarks>
    /// For an expansion board this follows its announcements and status reports. For the main board
    /// it is the state of the link to the controller, which is the only thing that can say whether it
    /// is there: unknown until the link first comes up, running while it is up, and timed out once it
    /// has gone.
    /// </remarks>
    public BoardState State
    {
        get => _state;
        set => SetPropertyValue(ref _state, value);
    }
    private BoardState _state;

    /// <summary>
    /// Indicates if this board supports external displays, or null if this board does not report it
    /// </summary>
    /// <remarks>
    /// Only the main board reports it, for the same reason as <see cref="FirmwareName"/>
    /// </remarks>
    public bool? SupportsDirectDisplay
    {
        get => _supportsDirectDisplay;
        set => SetPropertyValue(ref _supportsDirectDisplay, value);
    }
    private bool? _supportsDirectDisplay;

    /// <summary>
    /// Connection timeout of this board (in s), or null where the board has none
    /// </summary>
    /// <remarks>
    /// This is how long an expansion board may go without being heard from before it is given up on,
    /// which is what M959 sets and what <c>ExpansionManager</c>'s object model table reports. The
    /// main board has none: it is not on the CAN bus, and the link to it is watched by the link
    /// itself rather than by this timeout
    /// </remarks>
    public int? Timeout
    {
        get => _timeout;
        set => SetPropertyValue(ref _timeout, value);
    }
    private int? _timeout = DefaultConnectionTimeoutSeconds;

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

    /// <summary>
    /// Filename of the on-board WiFi chip or null if not present
    /// </summary>
    public string? WifiFirmwareFileName
    {
        get => _wifiFirmwareFileName;
        set => SetPropertyValue(ref _wifiFirmwareFileName, value);
    }
    private string? _wifiFirmwareFileName;
}
