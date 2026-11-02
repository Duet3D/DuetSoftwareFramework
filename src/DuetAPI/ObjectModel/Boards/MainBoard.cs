namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the mainboard, which is always the first item of the boards array
/// </summary>
public partial class MainBoard : Board
{
    /// <summary>
    /// Lowest CAN address served by CAN0 or null if CAN0 has no expansion boards assigned
    /// </summary>
    /// <remarks>
    /// Lower addresses and the firmware update address 126 stay on CAN1.
    /// Only boards with two CAN buses (6HC and 6XD) support this
    /// </remarks>
    public int? CanSplitAddress
    {
        get => _canSplitAddress;
        set => SetPropertyValue(ref _canSplitAddress, value);
    }
    private int? _canSplitAddress;

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
    /// Name of the firmware build
    /// </summary>
    public string FirmwareName
    {
        get => _firmwareName;
        set => SetPropertyValue(ref _firmwareName, value);
    }
    private string _firmwareName = string.Empty;

    /// <summary>
    /// Filename of the IAP binary that is used for updates from the SBC or null if unsupported
    /// </summary>
    [Verbose]
    public string? IapFileNameSBC
    {
        get => _iapFileNameSBC;
        set => SetPropertyValue(ref _iapFileNameSBC, value);
    }
    private string? _iapFileNameSBC;

    /// <summary>
    /// Filename of the IAP binary that is used for updates from the SD card or null if unsupported
    /// </summary>
    [Verbose]
    public string? IapFileNameSD
    {
        get => _iapFileNameSD;
        set => SetPropertyValue(ref _iapFileNameSD, value);
    }
    private string? _iapFileNameSD;

    /// <summary>
    /// Maximum number of heaters this board can control
    /// </summary>
    [Verbose]
    public int MaxHeaters
    {
        get => _maxHeaters;
        set => SetPropertyValue(ref _maxHeaters, value);
    }
    private int _maxHeaters;

    /// <summary>
    /// Indicates if this board supports external displays
    /// </summary>
    [Verbose]
    public bool SupportsDirectDisplay
    {
        get => _supportsDirectDisplay;
        set => SetPropertyValue(ref _supportsDirectDisplay, value);
    }
    private bool _supportsDirectDisplay;

    /// <summary>
    /// Filename of the on-board WiFi chip or null if not present
    /// </summary>
    [Verbose]
    public string? WifiFirmwareFileName
    {
        get => _wifiFirmwareFileName;
        set => SetPropertyValue(ref _wifiFirmwareFileName, value);
    }
    private string? _wifiFirmwareFileName;
}
