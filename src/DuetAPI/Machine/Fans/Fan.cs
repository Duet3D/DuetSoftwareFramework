using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Class representing information about an attached fan
    /// </summary>
    public sealed class Fan : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Value of the fan on a scale between 0 to 1
        /// </summary>
        public float Value
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
        private float _value;
        
        /// <summary>
        /// Name of the fan
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _name;
        
        /// <summary>
        /// Current RPM of this fan or null if unknown/unset
        /// </summary>
        public int? Rpm
        {
            get => _rpm;
            set
            {
                if (_rpm != value)
                {
                    _rpm = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _rpm;
        
        /// <summary>
        /// Whether the PWM signal of this fan is inverted
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
        /// Frequency of the fan (in Hz) or null if unknown/unset
        /// </summary>
        public float? Frequency
        {
            get => _frequency;
            set
            {
                if (_frequency != value)
                {
                    _frequency = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _frequency;
        
        /// <summary>
        /// Minimum value of this fan on a scale between 0 to 1
        /// </summary>
        public float Min
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
        private float _min;

        /// <summary>
        /// Maximum value of this fan on a scale between 0 to 1
        /// </summary>
        public float Max
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
        private float _max = 1.0F;

        /// <summary>
        /// Blip value indicating how long the fan is supposed to run at 100% when turning it on to get it started (in s)
        /// </summary>
        public float Blip
        {
            get => _blip;
            set
            {
                if (_blip != value)
                {
                    _blip = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _blip = 0.1F;
        
        /// <summary>
        /// Thermostatic control parameters
        /// </summary>
        public Thermostatic Thermostatic { get; private set; } = new Thermostatic();
        
        /// <summary>
        /// Pin number of the assigned fan or null if unknown/unset
        /// </summary>
        public int? Pin
        {
            get => _pin;
            set
            {
                if (_pin != value)
                {
                    _pin = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _pin;

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
            if (!(from is Fan other))
            {
                throw new ArgumentException("Invalid type");
            }

            Value = other.Value;
            Name = other.Name;
            Rpm = other.Rpm;
            Inverted = other.Inverted;
            Frequency = other.Frequency;
            Min = other.Min;
            Max = other.Max;
            Blip = other.Blip;
            Thermostatic.Assign(other.Thermostatic);
            Pin = other.Pin;
        }

        /// <summary>
        /// Creates a copy of this instance
        /// </summary>
        /// <returns>A copy of this instance</returns>
        public object Clone()
        {
            return new Fan
            {
                Value = Value,
                Name = Name,
                Rpm = Rpm,
                Inverted = Inverted,
                Frequency = Frequency,
                Min = Min,
                Max = Max,
                Blip = Blip,
                Thermostatic = (Thermostatic)Thermostatic.Clone(),
                Pin = Pin
            };
        }
    }
}