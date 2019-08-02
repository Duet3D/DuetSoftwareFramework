using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an attached laser diode
    /// </summary>
    public class Laser : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Actual PWM intensity (0..1)
        /// </summary>
        public float ActualPwm
        {
            get => _actualPwm;
            set
            {
                if (_actualPwm != value)
                {
                    _actualPwm = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _actualPwm;

        /// <summary>
        /// Requested PWM intensity from the G1 move (0..1)
        /// </summary>
        public float RequestedPwm
        {
            get => _requestedPwm;
            set
            {
                if (_requestedPwm != value)
                {
                    _requestedPwm = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _requestedPwm;

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
            if (!(from is Laser other))
            {
                throw new ArgumentException("Invalid type");
            }

            ActualPwm = other.ActualPwm;
            RequestedPwm = other.RequestedPwm;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            Laser clone = new Laser
            {
                ActualPwm = ActualPwm,
                RequestedPwm = RequestedPwm
            };

            return clone;
        }
    }
}
