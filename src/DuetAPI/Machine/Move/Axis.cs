using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a configured axis
    /// </summary>
    public sealed class Axis : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Letter of the axis (always upper-case)
        /// </summary>
        public char Letter
        {
            get => _letter;
            set
            {
                char newValue = char.ToUpperInvariant(value);
                if (_letter != newValue)
                {
                    _letter = newValue;
                    NotifyPropertyChanged();
                }
            }
        }
        private char _letter;

        /// <summary>
        /// Indices of the drives used
        /// </summary>
        public ObservableCollection<int> Drives { get; } = new ObservableCollection<int>();
        
        /// <summary>
        /// Whether or not the axis is homed
        /// </summary>
        public bool Homed
        {
            get => _homed;
            set
            {
                if (_homed != value)
                {
                    _homed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _homed;
        
        /// <summary>
        /// Current machine position (in mm or null if unknown/unset)
        /// </summary>
        public float? MachinePosition
        {
            get => _machinePosition;
            set
            {
                if (_machinePosition != value)
                {
                    _machinePosition = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _machinePosition;
        
        /// <summary>
        /// Minimum travel of this axis (in mm or null if unknown/unset)
        /// </summary>
        public float? Min
        {
            get => _min;
            set
            {
                if (_min != value)
                {
                    _min = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _min;

        /// <summary>
        /// Whether the axis minimum was probed
        /// </summary>
        public bool MinProbed
        {
            get => _minProbed;
            set
            {
                if (_minProbed != value)
                {
                    _minProbed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _minProbed;
        
        /// <summary>
        /// Maximum travel of this axis (in mm or null if unknown/unset)
        /// </summary>
        public float? Max
        {
            get => _max;
            set
            {
                if (_max != value)
                {
                    _max = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _max;

        /// <summary>
        /// Whether the axis maximum was probed
        /// </summary>
        public bool MaxProbed
        {
            get => _maxProbed;
            set
            {
                if (_maxProbed != value)
                {
                    _maxProbed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _maxProbed;
        
        /// <summary>
        /// Whether or not the axis is visible
        /// </summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible != value)
                {
                    _visible = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _visible = true;

        /// <summary>
        /// Assigns every property from another instance
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
            if (!(from is Axis other))
            {
                throw new ArgumentException("Invalid type");
            }

            Letter = other.Letter;
            ListHelpers.SetList(Drives, other.Drives);
            Homed = other.Homed;
            MachinePosition = other.MachinePosition;
            Min = other.Min;
            Max = other.Max;
            Visible = other.Visible;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Axis clone = new Axis
            {
                Letter = Letter,
                Homed = Homed,
                MachinePosition = MachinePosition,
                Min = Min,
                Max = Max,
                Visible = Visible
            };

            ListHelpers.AddItems(clone.Drives, Drives);

            return clone;
        }
    }
}
