using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Provides minimum, maximum and current values
    /// </summary>
    /// <typeparam name="T">ValueType of each property</typeparam>
    public sealed class MinMaxCurrent<T> : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Current value
        /// </summary>
        public T Current
        {
            get => _current;
            set
            {
                if (!_current.Equals(value))
                {
                    _current = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private T _current;

        /// <summary>
        /// Minimum value
        /// </summary>
        public T Min
        {
            get => _min;
            set
            {
                if (!_min.Equals(value))
                {
                    _min = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private T _min;

        /// <summary>
        /// Maximum value
        /// </summary>
        public T Max
        {
            get => _max;
            set
            {
                if (!_max.Equals(value))
                {
                    _max = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private T _max;

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
            if (!(from is MinMaxCurrent<T> other))
            {
                throw new ArgumentException("Invalid type");
            }

            Current = other.Current;
            Min = other.Min;
            Max = other.Max;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new MinMaxCurrent<T>
            {
                Current = Current,
                Min = Min,
                Max = Max
            };
        }
    }
}
