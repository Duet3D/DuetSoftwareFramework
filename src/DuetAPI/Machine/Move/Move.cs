using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the move subsystem
    /// </summary>
    public sealed class Move : IAssignable, ICloneable, INotifyPropertyChanged
    {
        /// <summary>
        /// Event to trigger when a property has changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// List of the configured axes
        /// </summary>
        /// <seealso cref="Axis"/>
        public ObservableCollection<Axis> Axes { get; } = new ObservableCollection<Axis>();
        
        /// <summary>
        /// Current babystep amount in Z direction (in mm)
        /// </summary>
        public float BabystepZ
        {
            get => _babystepZ;
            set
            {
                if (_babystepZ != value)
                {
                    _babystepZ = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _babystepZ;
        
        /// <summary>
        /// Information about the current move
        /// </summary>
        public CurrentMove CurrentMove { get; private set; } = new CurrentMove();

        /// <summary>
        /// Name of the currently used bed compensation
        /// </summary>
        public string Compensation
        {
            get => _compensation;
            set
            {
                if (_compensation != value)
                {
                    _compensation = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _compensation = "None";
        
        /// <summary>
        /// List of configured drives
        /// </summary>
        /// <seealso cref="Drive"/>
        public ObservableCollection<Drive> Drives { get; } = new ObservableCollection<Drive>();
        
        /// <summary>
        /// List of configured extruders
        /// </summary>
        /// <seealso cref="Extruder"/>
        public ObservableCollection<Extruder> Extruders { get; } = new ObservableCollection<Extruder>();
        
        /// <summary>
        /// Information about the currently configured geometry
        /// </summary>
        public Geometry Geometry { get; private set; } = new Geometry();
        
        /// <summary>
        /// Idle current reduction parameters
        /// </summary>
        public MotorsIdleControl Idle { get; private set; } = new MotorsIdleControl();
        
        /// <summary>
        /// Speed factor applied to every regular move (1.0 equals 100%)
        /// </summary>
        public float SpeedFactor
        {
            get => _speedFactor;
            set
            {
                if (_speedFactor != value)
                {
                    _speedFactor = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _speedFactor = 1.0F;

        /// <summary>
        /// Index of the currently selected workspace
        /// </summary>
        public int CurrentWorkplace
        {
            get => _currentWorkplace;
            set
            {
                if (_currentWorkplace != value)
                {
                    _currentWorkplace = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _currentWorkplace;

        /// <summary>
        /// Axis offsets of each available workspace (in mm)
        /// </summary>
        /// <remarks>When modifying items, make sure to (re-)set an entire item to ensure the change events are called</remarks>
        public ObservableCollection<float[]> WorkplaceCoordinates { get; } = new ObservableCollection<float[]>();

        /// <summary>
        /// Assigns every property of another instance of this one
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void Assign(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is Move other))
            {
                throw new ArgumentException("Invalid type");
            }

            ListHelpers.AssignList(Axes, other.Axes);
            BabystepZ = other.BabystepZ;
            CurrentMove.Assign(other.CurrentMove);
            Compensation = other.Compensation;
            ListHelpers.AssignList(Drives, other.Drives);
            ListHelpers.AssignList(Extruders, other.Extruders);
            Geometry.Assign(other.Geometry);
            Idle.Assign(other.Idle);
            SpeedFactor = other.SpeedFactor;
            CurrentWorkplace = other.CurrentWorkplace;
            ListHelpers.SetList(WorkplaceCoordinates, other.WorkplaceCoordinates);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Move clone = new Move
            {
                BabystepZ = BabystepZ,
                CurrentMove = (CurrentMove)CurrentMove.Clone(),
                Compensation = Compensation,
                Geometry = (Geometry)Geometry.Clone(),
                Idle = (MotorsIdleControl)Idle.Clone(),
                SpeedFactor = SpeedFactor
            };

            ListHelpers.CloneItems(clone.Axes, Axes);
            ListHelpers.CloneItems(clone.Drives, Drives);
            ListHelpers.CloneItems(clone.Extruders, Extruders);
            ListHelpers.AddItems(clone.WorkplaceCoordinates, WorkplaceCoordinates);

            return clone;
        }
    }
}
