using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public sealed class HeaterModel : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Gain value or null if unknown
        /// </summary>
        public float? Gain
        {
            get => _gain;
            set
            {
                if (_gain != value)
                {
                    _gain = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _gain;
        
        /// <summary>
        /// Time constant or null if unknown
        /// </summary>
        public float? TimeConstant
        {
            get => _timeConstant;
            set
            {
                if (_timeConstant != value)
                {
                    _timeConstant = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _timeConstant;
        
        /// <summary>
        /// Dead time of this heater or null if unknown
        /// </summary>
        public float? DeadTime
        {
            get => _deadTime;
            set
            {
                if (_deadTime != value)
                {
                    _deadTime = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _deadTime;
        
        /// <summary>
        /// Maximum PWM or null if unknown
        /// </summary>
        public float? MaxPwm
        {
            get => _maxPwm;
            set
            {
                if (_maxPwm != value)
                {
                    _maxPwm = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _maxPwm;
        
        /// <summary>
        /// Standard voltage of this heater or null if unknown
        /// </summary>
        public float? StandardVoltage
        {
            get => _standardVoltage;
            set
            {
                if (_standardVoltage != value)
                {
                    _standardVoltage = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _standardVoltage;

        /// <summary>
        /// Indicates if PID control is being used
        /// </summary>
        public bool UsePID
        {
            get => _usePID;
            set
            {
                if (_usePID != value)
                {
                    _usePID = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _usePID = true;

        /// <summary>
        /// Indicates if custom PID values are used
        /// </summary>
        public bool CustomPID
        {
            get => _customPID;
            set
            {
                if (_customPID != value)
                {
                    _customPID = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _customPID;

        /// <summary>
        /// Proportional value of the PID regulator
        /// </summary>
        public float P
        {
            get => _p;
            set
            {
                if (_p != value)
                {
                    _p = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _p;

        /// <summary>
        /// Integral value of the PID regulator
        /// </summary>
        public float I
        {
            get => _i;
            set
            {
                if (_i != value)
                {
                    _i = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _i;

        /// <summary>
        /// Derivative value of the PID regulator
        /// </summary>
        public float D
        {
            get => _d;
            set
            {
                if (_d != value)
                {
                    _d = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _d;

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
            if (!(from is HeaterModel other))
            {
                throw new ArgumentException("Invalid type");
            }

            Gain = other.Gain;
            TimeConstant = other.TimeConstant;
            DeadTime = other.DeadTime;
            MaxPwm = other.MaxPwm;
            StandardVoltage = other.StandardVoltage;
            UsePID = other.UsePID;
            CustomPID = other.CustomPID;
            P = other.P;
            I = other.I;
            D = other.D;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new HeaterModel
            {
                Gain = Gain,
                TimeConstant = TimeConstant,
                DeadTime = DeadTime,
                MaxPwm = MaxPwm,
                StandardVoltage = StandardVoltage,
                UsePID = UsePID,
                CustomPID = CustomPID,
                P = P,
                I = I,
                D = D
            };
        }
    }
}