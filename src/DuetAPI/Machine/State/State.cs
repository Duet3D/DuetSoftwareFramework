﻿namespace DuetAPI.Machine
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
        public string DsfVersion
        {
            get => _dsfVersion;
            set => SetPropertyValue(ref _dsfVersion, value);
        }
        private string _dsfVersion;

        /// <summary>
        /// Current laser PWM (0..1) or null if not applicable
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
        public string LogFile
        {
            get => _logFile;
            set => SetPropertyValue(ref _logFile, value);
        }
        private string _logFile;

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
        /// Number of the next tool to be selected
        /// </summary>
        public int NextTool
        {
            get => _nextTool;
			set => SetPropertyValue(ref _nextTool, value);
        }
        private int _nextTool = -1;

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
        private MachineStatus _status = MachineStatus.Idle;

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