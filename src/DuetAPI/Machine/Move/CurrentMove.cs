using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current move
    /// </summary>
    public sealed class CurrentMove : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Requested speed of the current move (in mm/s)
        /// </summary>
        public float RequestedSpeed
        {
            get => _requestedSpeed;
            set
            {
                if (_requestedSpeed != value)
                {
                    _requestedSpeed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _requestedSpeed;
        
        /// <summary>
        /// Top speed of the current move (in mm/s)
        /// </summary>
        public float TopSpeed
        {
            get => _topSpeed;
            set
            {
                if (_topSpeed != value)
                {
                    _topSpeed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _topSpeed;

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
            if (!(from is CurrentMove other))
            {
                throw new ArgumentException("Invalid type");
            }

            RequestedSpeed = other.RequestedSpeed;
            TopSpeed = other.TopSpeed;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new CurrentMove
            {
                RequestedSpeed = RequestedSpeed,
                TopSpeed = TopSpeed
            };
        }
    }
}