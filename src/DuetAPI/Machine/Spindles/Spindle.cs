using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a CNC spindle
    /// </summary>
    public sealed class Spindle : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Active RPM
        /// </summary>
        public float Active
        {
            get => _active;
            set
            {
                if (_active != value)
                {
                    _active = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _active;
        
        /// <summary>
        /// Current RPM
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
            if (!(from is Spindle other))
            {
                throw new ArgumentException("Invalid type");
            }

            Active = other.Active;
            Current = other.Current;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Spindle
            {
                Active = Active,
                Current = Current
            };
        }
    }
}