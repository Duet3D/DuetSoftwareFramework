using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a drive
    /// </summary>
    public sealed class Drive : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Current user position of this drive (in mm)
        /// </summary>
        public float? Position
        {
            get => _position;
            set
            {
                if (_position != value)
                {
                    _position = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _position;
        
        /// <summary>
        /// Information about the configured microstepping
        /// </summary>
        public DriveMicrostepping Microstepping { get; private set; } = new DriveMicrostepping();
        
        /// <summary>
        /// Configured current of this drive (in mA)
        /// </summary>
        public int? Current
        {
            get => _current;
            set
            {
                if (_current != value)
                {
                    _current = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _current;
        
        /// <summary>
        /// Acceleration of this drive (in mm/s^2)
        /// </summary>
        public float? Acceleration
        {
            get => _acceleration;
            set
            {
                if (_acceleration != value)
                {
                    _acceleration = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _acceleration;
        
        /// <summary>
        /// Minimum allowed speed for this drive (in mm/s)
        /// </summary>
        public float? MinSpeed
        {
            get => _minSpeed;
            set
            {
                if (_minSpeed != value)
                {
                    _minSpeed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _minSpeed;
        
        /// <summary>
        /// Maximum allowed speed for this drive (in mm/s)
        /// </summary>
        public float? MaxSpeed
        {
            get => _maxSpeed;
            set
            {
                if (_maxSpeed != value)
                {
                    _maxSpeed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _maxSpeed;

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
            if (!(from is Drive other))
            {
                throw new ArgumentException("Invalid type");
            }

            Position = other.Position;
            Microstepping.AssignFrom(other.Microstepping);
            Current = other.Current;
            Acceleration = other.Acceleration;
            MinSpeed = other.MinSpeed;
            MaxSpeed = other.MaxSpeed;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Drive
            {
                Position = Position,
                Microstepping = (DriveMicrostepping)Microstepping.Clone(),
                Current = Current,
                Acceleration = Acceleration,
                MinSpeed = MinSpeed,
                MaxSpeed = MaxSpeed
            };
        }
    }
}