namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the mainboard, which is always the first item of the boards array
/// </summary>
public partial class MainBoard : Board
{
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
    public string? IapFileNameSBC
    {
        get => _iapFileNameSBC;
        set => SetPropertyValue(ref _iapFileNameSBC, value);
    }
    private string? _iapFileNameSBC;

    /// <summary>
    /// Filename of the IAP binary that is used for updates from the SD card or null if unsupported
    /// </summary>
    public string? IapFileNameSD
    {
        get => _iapFileNameSD;
        set => SetPropertyValue(ref _iapFileNameSD, value);
    }
    private string? _iapFileNameSD;

    /// <summary>
    /// Maximum number of heaters this board can control
    /// </summary>
    public int MaxHeaters
    {
        get => _maxHeaters;
        set => SetPropertyValue(ref _maxHeaters, value);
    }
    private int _maxHeaters;

    /// <summary>
    /// Indicates if this board supports external displays
    /// </summary>
    public bool SupportsDirectDisplay
    {
        get => _supportsDirectDisplay;
        set => SetPropertyValue(ref _supportsDirectDisplay, value);
    }
    private bool _supportsDirectDisplay;

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
