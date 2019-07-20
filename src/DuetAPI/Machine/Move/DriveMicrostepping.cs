using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about configured microstepping
    /// </summary>
    public sealed class DriveMicrostepping : ICloneable, INotifyPropertyChanged
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
        /// Microstepping value (e.g. x16)
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
        private int _value = 16;
        
        /// <summary>
        /// Whether the microstepping is interpolated
        /// </summary>
        /// <remarks>
        /// This may not be supported on all boards.
        /// </remarks>
        public bool Interpolated
        {
            get => _interpolated;
            set
            {
                if (_interpolated != value)
                {
                    _interpolated = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _interpolated = true;

        /// <summary>
        /// Assigns every property from another instance
        /// </summary>
        /// <param name="from">Object to assign from</param>
        /// <exception cref="ArgumentNullException">other is null</exception>
        /// <exception cref="ArgumentException">Types do not match</exception>
        public void AssignFrom(object from)
        {
            if (from == null)
            {
                throw new ArgumentNullException();
            }
            if (!(from is DriveMicrostepping other))
            {
                throw new ArgumentException("Invalid type");
            }

            Value = other.Value;
            Interpolated = other.Interpolated;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new DriveMicrostepping
            {
                Value = Value,
                Interpolated = Interpolated
            };
        }
    }
}