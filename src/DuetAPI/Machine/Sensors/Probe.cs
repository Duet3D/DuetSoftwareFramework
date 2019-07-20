using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a configured probe
    /// </summary>
    public sealed class Probe : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Type of the configured probe
        /// </summary>
        /// <seealso cref="ProbeType"/>
        public ProbeType Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private ProbeType _type;
        
        /// <summary>
        /// Current analog value of the probe
        /// </summary>
        public int Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _value;

        /// <summary>
        /// Secondary value(s) of the probe
        /// </summary>
        public ObservableCollection<int> SecondaryValues { get; } = new ObservableCollection<int>();
        
        /// <summary>
        /// Configured trigger threshold (0..1023)
        /// </summary>
        public int Threshold
        {
            get => _threshold;
            set
            {
                if (_threshold != value)
                {
                    _threshold = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _threshold = 500;
        
        /// <summary>
        /// Probe speed (in mm/s)
        /// </summary>
        public float Speed
        {
            get => _speed;
            set
            {
                if (_speed != value)
                {
                    _speed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _speed = 2.0F;
        
        /// <summary>
        /// Dive height (in mm)
        /// </summary>
        public float DiveHeight
        {
            get => _diveHeight;
            set
            {
                if (_diveHeight != value)
                {
                    _diveHeight = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _diveHeight;

        /// <summary>
        /// X+Y offsets (in mm)
        /// </summary>
        public ObservableCollection<float> Offsets { get; } = new ObservableCollection<float>() { 0.0F, 0.0F };

        /// <summary>
        /// Z height at which the probe is triggered (in mm)
        /// </summary>
        public float TriggerHeight
        {
            get => _triggerHeight;
            set
            {
                if (_triggerHeight != value)
                {
                    _triggerHeight = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _triggerHeight = 0.7F;

        /// <summary>
        /// Whether the probe signal is filtered
        /// </summary>
        public bool Filtered
        {
            get => _filtered;
            set
            {
                if (_filtered != value)
                {
                    _filtered = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _filtered = true;

        /// <summary>
        /// Whether the probe signal is inverted
        /// </summary>
        public bool Inverted
        {
            get => _inverted;
            set
            {
                if (_inverted != value)
                {
                    _inverted = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _inverted;
        
        /// <summary>
        /// Recovery time (in s)
        /// </summary>
        public float RecoveryTime
        {
            get => _recoveryTime;
            set
            {
                if (_recoveryTime != value)
                {
                    _recoveryTime = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _recoveryTime;
        
        /// <summary>
        /// Travel speed when probing multiple points (in mm/s)
        /// </summary>
        public float TravelSpeed
        {
            get => _travelSpeed;
            set
            {
                if (_travelSpeed != value)
                {
                    _travelSpeed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _travelSpeed = 100.0F;
        
        /// <summary>
        /// Maximum number of times to probe after a bad reading was determined
        /// </summary>
        public int MaxProbeCount
        {
            get => _maxProbeCount;
            set
            {
                if (_maxProbeCount != value)
                {
                    _maxProbeCount = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _maxProbeCount = 1;
        
        /// <summary>
        /// Allowed tolerance deviation between two measures (in mm)
        /// </summary>
        public float Tolerance
        {
            get => _tolerance;
            set
            {
                if (_tolerance != value)
                {
                    _tolerance = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _tolerance = 0.03F;
        
        /// <summary>
        /// Whether probing disables the bed heater(s)
        /// </summary>
        public bool DisablesBed
        {
            get => _disablesBed;
            set
            {
                if (_disablesBed != value)
                {
                    _disablesBed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _disablesBed;

        /// <summary>
        /// Indicates if the probe parameters are supposed to be saved to config-override.g
        /// </summary>
        public bool Persistent
        {
            get => _persistent;
            set
            {
                if (_persistent != value)
                {
                    _persistent = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _persistent;

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
            if (!(from is Probe other))
            {
                throw new ArgumentException("Invalid type");
            }

            Type = other.Type;
            Value = other.Value;
            ListHelpers.SetList(SecondaryValues, other.SecondaryValues);
            Threshold = other.Threshold;
            Speed = other.Speed;
            DiveHeight = other.DiveHeight;
            ListHelpers.SetList(Offsets, other.Offsets);
            TriggerHeight = other.TriggerHeight;
            Filtered = other.Filtered;
            Inverted = other.Inverted;
            RecoveryTime = other.RecoveryTime;
            TravelSpeed = other.TravelSpeed;
            MaxProbeCount = other.MaxProbeCount;
            Tolerance = other.Tolerance;
            DisablesBed = other.DisablesBed;
            Persistent = other.Persistent;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Probe clone = new Probe
            {
                Type = Type,
                Value = Value,
                Threshold = Threshold,
                Speed = Speed,
                DiveHeight = DiveHeight,
                TriggerHeight = TriggerHeight,
                Filtered = Filtered,
                Inverted = Inverted,
                RecoveryTime = RecoveryTime,
                TravelSpeed = TravelSpeed,
                MaxProbeCount = MaxProbeCount,
                Tolerance = Tolerance,
                DisablesBed = DisablesBed,
                Persistent = Persistent
            };

            ListHelpers.AddItems(clone.SecondaryValues, SecondaryValues);
            ListHelpers.SetList(clone.Offsets, Offsets);

            return clone;
        }
    }
}