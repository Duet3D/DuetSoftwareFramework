using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
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
        /// Limit axis positions by their minima and maxima
        /// </summary>
        public bool LimitAxes
        {
            get => _limitAxes;
            set => SetPropertyValue(ref _limitAxes, value);
        }
        private bool _limitAxes = true;

        /// <summary>
        /// Indicates if standard moves are forbidden if the corresponding axis is not homed
        /// </summary>
        public bool NoMovesBeforeHoming
        {
            get => _noMovesBeforeHoming;
            set => SetPropertyValue(ref _noMovesBeforeHoming, value);
        }
        private bool _noMovesBeforeHoming = true;

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
        /// List of move queue items (DDA rings)
        /// </summary>
        public ModelCollection<MoveQueueItem> Queue { get; } = new ModelCollection<MoveQueueItem>();

        /// <summary>
        /// Parameters for centre rotation
        /// </summary>
        public MoveRotation Rotation { get; } = new MoveRotation();

        /// <summary>
        /// Parameters for input shaping
        /// </summary>
        public InputShaping Shaping { get; } = new InputShaping();

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

        /// <summary>
        /// Index of the currently selected workspace
        /// </summary>
        [JsonIgnore]
        [Obsolete("Use WorkplaceNumber instead")]
        public int WorkspaceNumber
        {
            get => _workplaceNumber + 1;
            set => _workplaceNumber = value - 1;
        }
    }
}
