using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an extra heater (virtual)
    /// </summary>
    public sealed class ExtraHeater : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Current temperature (in degC)
        /// </summary>
        public float Current
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
        private float _current;
        
        /// <summary>
        /// Name of the extra heater
        /// </summary>
        /// <remarks>
        /// This must not be set to null
        /// </remarks>
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
        /// State of the extra heater or null if unknown/unset
        /// </summary>
        public HeaterState? State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private HeaterState? _state;
        
        /// <summary>
        /// Sensor number (thermistor index) of the extra heater or null if unknown/unset
        /// </summary>
        public int? Sensor
        {
            get => _sensor;
            set
            {
                if (_sensor != value)
                {
                    _sensor = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _sensor;

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
            if (!(from is ExtraHeater other))
            {
                throw new ArgumentException("Invalid type");
            }

            Current = other.Current;
            Name = (other.Name != null) ? string.Copy(other.Name) : null;
            State = other.State;
            Sensor = other.Sensor;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExtraHeater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Sensor = Sensor
            };
        }
    }
}