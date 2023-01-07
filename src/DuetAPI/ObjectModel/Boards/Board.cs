using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a connected board
    /// </summary>
    public sealed class Board : ModelObject
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
        /// Filename of the bootloader binary or null if unknown
        /// </summary>
        public string? BootloaderFileName
        {
            get => _bootloaderFileName;
            set => SetPropertyValue(ref _bootloaderFileName, value);
        }
        private string? _bootloaderFileName;

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
        public ClosedLoop? ClosedLoop
        {
            get => _closedLoop;
            set => SetPropertyValue(ref _closedLoop, value);
        }
        private ClosedLoop? _closedLoop;

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
        /// Date of the firmware build
        /// </summary>
        public string FirmwareDate
        {
            get => _firmwareDate;
            set => SetPropertyValue(ref _firmwareDate, value);
        }
        private string _firmwareDate = string.Empty;

        /// <summary>
        /// Filename of the firmware binary or null if unknown
        /// </summary>
        public string? FirmwareFileName
        {
            get => _firmwareFileName;
            set => SetPropertyValue(ref _firmwareFileName, value);
        }
        private string? _firmwareFileName;

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
        /// Maximum number of heaters this board can control
        /// </summary>
        public int MaxHeaters
        {
            get => _maxHeaters;
            set => SetPropertyValue(ref _maxHeaters, value);
        }
        private int _maxHeaters;

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
        public MinMaxCurrent<float>? McuTemp
        {
            get => _mcuTemp;
            set => SetPropertyValue(ref _mcuTemp, value);
        }
        private MinMaxCurrent<float>? _mcuTemp;

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
        public BoardState State
        {
            get => _state;
            set => SetPropertyValue(ref _state, value);
        }
        private BoardState _state;

        /// <summary>
        /// Indicates if this board supports external 12864 displays
        /// </summary>
        [Obsolete("Replaced with SupportsDirectDisplay")]
        public bool Supports12864
        {
            get => _supportsDirectDisplay;
            set => SetPropertyValue(ref _supportsDirectDisplay, value);
        }

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
        public MinMaxCurrent<float>? V12
        {
            get => _v12;
            set => SetPropertyValue(ref _v12, value);
        }
        private MinMaxCurrent<float>? _v12;

        /// <summary>
        /// Minimum, maximum, and current voltages on the input rail or null if unknown
        /// </summary>
        public MinMaxCurrent<float>? VIn
        {
            get => _vIn;
            set => SetPropertyValue(ref _vIn, value);
        }
        private MinMaxCurrent<float>? _vIn;

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
}
