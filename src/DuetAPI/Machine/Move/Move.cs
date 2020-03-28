namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the move subsystem
    /// </summary>
    public sealed class Move : ModelObject
    {
        /// <summary>
        /// List of the configured axes
        /// </summary>
        /// <seealso cref="Axis"/>
        public ModelCollection<Axis> Axes { get; } = new ModelCollection<Axis>();

        /// <summary>
        /// Information about the automatic calibration
        /// </summary>
        public MoveCalibration Calibration { get; } = new MoveCalibration();

        /// <summary>
        /// Information about the currently configured compensation options
        /// </summary>
        public MoveCompensation Compensation { get; } = new MoveCompensation();
        
        /// <summary>
        /// Information about the current move
        /// </summary>
        public CurrentMove CurrentMove { get; } = new CurrentMove();

        /// <summary>
        /// Information about the configured dynamic acceleration adjustment
        /// </summary>
        public DAA DAA { get; } = new DAA();

        /// <summary>
        /// List of configured extruders
        /// </summary>
        /// <seealso cref="Extruder"/>
        public ModelCollection<Extruder> Extruders { get; } = new ModelCollection<Extruder>();
        
        /// <summary>
        /// Idle current reduction parameters
        /// </summary>
        public MotorsIdleControl Idle { get; } = new MotorsIdleControl();

        /// <summary>
        /// Configured kinematics options
        /// </summary>
        public Kinematics Kinematics
        {
            get => _kinematics;
			set => SetPropertyValue(ref _kinematics, value);
        }
        private Kinematics _kinematics = new Kinematics();

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
        /// Index of the currently selected workspace
        /// </summary>
        public int WorkspaceNumber
        {
            get => _workspaceNumber;
			set => SetPropertyValue(ref _workspaceNumber, value);
        }
        private int _workspaceNumber;
    }
}
