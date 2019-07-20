using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Idle factor parameters for automatic motor current reduction
    /// </summary>
    public sealed class MotorsIdleControl : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Idle timeout after which the stepper motor currents are reduced (in s)
        /// </summary>
        public float Timeout
        {
            get => _timeout;
            set
            {
                if (_timeout != value)
                {
                    _timeout = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _timeout = 30.0F;
        
        /// <summary>
        /// Motor current reduction factor (on a scale between 0 to 1)
        /// </summary>
        public float Factor
        {
            get => _factor;
            set
            {
                if (_factor != value)
                {
                    _factor = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _factor = 0.3F;

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
            if (!(from is MotorsIdleControl other))
            {
                throw new ArgumentException("Invalid type");
            }

            Timeout = other.Timeout;
            Factor = other.Factor;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new MotorsIdleControl
            {
                Timeout = Timeout,
                Factor = Factor
            };
        }
    }
}