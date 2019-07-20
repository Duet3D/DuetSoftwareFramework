using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the 3D scanner subsystem
    /// </summary>
    public sealed class Scanner : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Progress of the current action (on a scale between 0 to 1)
        /// </summary>
        /// <remarks>
        /// Previous status responses used a scale of 0..100
        /// </remarks>
        public float Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _progress;
        
        /// <summary>
        /// Status of the 3D scanner
        /// </summary>
        /// <seealso cref="ScannerStatus"/>
        public ScannerStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private ScannerStatus _status = ScannerStatus.Disconnected;

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
            if (!(from is Scanner other))
            {
                throw new ArgumentException("Invalid type");
            }

            Progress = other.Progress;
            Status = other.Status;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Scanner
            {
                Progress = Progress,
                Status = Status
            };
        }
    }
}