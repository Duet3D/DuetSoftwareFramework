using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Thermostatic parameters of a fan
    /// </summary>
    public sealed class Thermostatic : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Defines whether thermostatic control is enabled
        /// </summary>
        public bool Control
        {
            get => _control;
            set
            {
                if (_control != value)
                {
                    _control = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _control = true;

        /// <summary>
        /// The heaters to monitor (indices)
        /// </summary>
        public ObservableCollection<int> Heaters { get; } = new ObservableCollection<int>();
        
        /// <summary>
        /// Minimum temperature required to turn on the fan (in degC or null if unknown)
        /// </summary>
        public float? Temperature
        {
            get => _temperature;
            set
            {
                if (_temperature != value)
                {
                    _temperature = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _temperature;

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
            if (!(from is Thermostatic other))
            {
                throw new ArgumentException("Invalid type");
            }

            Control = other.Control;
            ListHelpers.SetList(Heaters, other.Heaters);
            Temperature = other.Temperature;
        }

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Thermostatic clone = new Thermostatic
            {
                Control = Control,
                Temperature = Temperature
            };

            ListHelpers.AddItems(clone.Heaters, Heaters);

            return clone;
        }
    }
}