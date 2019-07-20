using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a heater
    /// </summary>
    public sealed class Heater : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Current temperature of the heater (in C)
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
        private float _current = -273.15F;
        
        /// <summary>
        /// Name of the heater or null if unset
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
        /// State of the heater
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
        /// Information about the heater model
        /// </summary>
        public HeaterModel Model { get; private set; } = new HeaterModel();
        
        /// <summary>
        /// Maximum allowed temperature for this heater (in C)
        /// </summary>
        /// <remarks>
        /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
        /// </remarks>
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
        /// Sensor number (thermistor index) of this heater or null if unknown
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
            if (!(from is Heater other))
            {
                throw new ArgumentException("Invalid type");
            }

            Current = other.Current;
            Name = (other.Name != null) ? string.Copy(other.Name) : null;
            State = other.State;
            Model.Assign(other.Model);
            Max = other.Max;
            Sensor = other.Sensor;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Heater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Model = (HeaterModel)Model.Clone(),
                Max = Max,
                Sensor = Sensor
            };
        }
    }
}