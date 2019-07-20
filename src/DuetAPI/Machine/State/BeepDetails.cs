using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Details about a requested beep
    /// </summary>
    public sealed class BeepDetails : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Frequency of the requested beep
        /// </summary>
        public int Frequency
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
        private int _frequency;

        /// <summary>
        /// Duration of the requested beep (in ms)
        /// </summary>
        public float Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _duration;

        /// <summary>
        /// Assigns every property of another instance of this one
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
            if (!(from is BeepDetails other))
            {
                throw new ArgumentException("Invalid type");
            }

            Frequency = other.Frequency;
            Duration = other.Duration;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            return new BeepDetails
            {
                Frequency = Frequency,
                Duration = Duration
            };
        }
    }
}
