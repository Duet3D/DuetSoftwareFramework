using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the machine state
    /// </summary>
    public sealed class State : ModelObject
    {
        /// <summary>
        /// State of the ATX power pin (if controlled)
        /// </summary>
        public bool? AtxPower
        {
            get => _atxPower;
            set => SetPropertyValue(ref _atxPower, value);
        }
        private bool? _atxPower;

        /// <summary>
        /// Information about a requested beep or null if none is requested
        /// </summary>
        public BeepRequest Beep
        {
            get => _beep;
            set => SetPropertyValue(ref _beep, value);
        }
        private BeepRequest _beep;

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
        /// Persistent message to display (see M117)
        /// </summary>
        public string DisplayMessage
        {
            get => _displayMessage;
            set => SetPropertyValue(ref _displayMessage, value);
        }
        private string _displayMessage = string.Empty;

        /// <summary>
        /// Version of the Duet Software Framework package
        /// </summary>
        [SbcProperty(false)]
        public string DsfVersion
        {
            get => _dsfVersion;
            set => SetPropertyValue(ref _dsfVersion, value);
        }
        private string _dsfVersion;

        /// <summary>
        /// Indicates if DSF allows the installation and usage of third-party plugins
        /// </summary>
        [SbcProperty(false)]
        public bool DsfPluginSupport
        {
            get => _dsfPluginSupport;
            set => SetPropertyValue(ref _dsfPluginSupport, value);
        }
        private bool _dsfPluginSupport;

        /// <summary>
        /// Indicates if DSF allows the installation and usage of third-party root plugins (potentially dangerous)
        /// </summary>
        [SbcProperty(false)]
        public bool DsfRootPluginSupport
        {
            get => _dsfRootPluginSupport;
            set => SetPropertyValue(ref _dsfRootPluginSupport, value);
        }
        private bool _dsfRootPluginSupport;

        /// <summary>
        /// List of general-purpose output ports
        /// </summary>
        /// <seealso cref="GpOutputPort"/>
        public ModelCollection<GpOutputPort> GpOut { get; } = new ModelCollection<GpOutputPort>();

        /// <summary>
        /// Laser PWM of the next commanded move (0..1) or null if not applicable
        /// </summary>
        public float? LaserPwm
        {
            get => _laserPwm;
            set => SetPropertyValue(ref _laserPwm, value);
        }
        private float? _laserPwm = null;

        /// <summary>
        /// Log file being written to or null if logging is disabled
        /// </summary>
        [SbcProperty(true)]
        public string LogFile
        {
            get => _logFile;
            set => SetPropertyValue(ref _logFile, value);
        }
        private string _logFile;

        /// <summary>
        /// Current log level
        /// </summary>
        [SbcProperty(true)]
        public LogLevel LogLevel
        {
            get => _logLevel;
            set => SetPropertyValue(ref _logLevel, value);
        }
        private LogLevel _logLevel = LogLevel.Off;

        /// <summary>
        /// Details about a requested message box or null if none is requested
        /// </summary>
        public MessageBox MessageBox
        {
            get => _messageBox;
            set => SetPropertyValue(ref _messageBox, value);
        }
        private MessageBox _messageBox;

        /// <summary>
        /// Current mode of operation
        /// </summary>
        public MachineMode MachineMode
        {
            get => _machineMode;
			set => SetPropertyValue(ref _machineMode, value);
        }
        private MachineMode _machineMode = MachineMode.FFF;

        /// <summary>
        /// Indicates if the current macro file was restarted after a pause
        /// </summary>
        public bool MacroRestarted
        {
            get => _macroRestarted;
            set => SetPropertyValue(ref _macroRestarted, value);
        }
        private bool _macroRestarted;

        /// <summary>
        /// Millisecond fraction of <see cref="UpTime"/>
        /// </summary>
        public int MsUpTime
        {
            get => _msUpTime;
            set => SetPropertyValue(ref _msUpTime, value);
        }
        private int _msUpTime;

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
        /// Indicates if at least one plugin has been started
        /// </summary>
        public bool PluginsStarted
        {
            get => _pluginsStarted;
            set => SetPropertyValue(ref _pluginsStarted, value);
        }
        private bool _pluginsStarted;

        /// <summary>
        /// Script to execute when the power fails
        /// </summary>
        public string PowerFailScript
        {
            get => _powerFailScript;
			set => SetPropertyValue(ref _powerFailScript, value);
        }
        private string _powerFailScript = string.Empty;

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
        /// List of restore points
        /// </summary>
        public ModelCollection<RestorePoint> RestorePoints { get; } = new ModelCollection<RestorePoint>();

        /// <summary>
        /// Current state of the machine
        /// </summary>
        public MachineStatus Status
        {
            get => _status;
			set => SetPropertyValue(ref _status, value);
        }
        private MachineStatus _status = MachineStatus.Starting;

        /// <summary>
        /// Internal date and time in RepRapFirmware or null if unknown
        /// </summary>
        public DateTime? Time
        {
            get => _time;
            set => SetPropertyValue(ref _time, value);
        }
        private DateTime? _time;

        /// <summary>
        /// How long the machine has been running (in s)
        /// </summary>
        public int UpTime
        {
            get => _upTime;
			set => SetPropertyValue(ref _upTime, value);
        }
        private int _upTime;
    }
}