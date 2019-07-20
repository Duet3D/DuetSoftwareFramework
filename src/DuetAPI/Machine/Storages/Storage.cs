using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a storage device
    /// </summary>
    public sealed class Storage : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Whether the storage device is mounted
        /// </summary>
        public bool Mounted
        {
            get => _mounted;
            set
            {
                if (_mounted != value)
                {
                    _mounted = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _mounted;
        
        /// <summary>
        /// Speed of the storage device (in bytes/s or null if unknown)
        /// </summary>
        public int? Speed
        {
            get => _speed;
            set
            {
                if (_speed != value)
                {
                    _speed = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _speed;
        
        /// <summary>
        /// Total capacity of the storage device (in bytes)
        /// </summary>
        public long? Capacity
        {
            get => _capacity;
            set
            {
                if (_capacity != value)
                {
                    _capacity = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long? _capacity;
        
        /// <summary>
        /// How much space is still available on this device (in bytes)
        /// </summary>
        public long? Free
        {
            get => _free;
            set
            {
                if (_free != value)
                {
                    _free = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long? _free;
        
        /// <summary>
        /// Number of currently open files or null if unknown
        /// </summary>
        public int? OpenFiles
        {
            get => _openFiles;
            set
            {
                if (_openFiles != value)
                {
                    _openFiles = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _openFiles;
        
        /// <summary>
        /// Logical path of the storage device
        /// </summary>
        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _path;

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
            if (!(from is Storage other))
            {
                throw new ArgumentException("Invalid type");
            }

            Mounted = other.Mounted;
            Speed = other.Speed;
            Capacity = other.Capacity;
            Free = other.Free;
            OpenFiles = other.OpenFiles;
            Path = other.Path;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Storage
            {
                Mounted = Mounted,
                Speed = Speed,
                Capacity = Capacity,
                Free = Free,
                OpenFiles = OpenFiles,
                Path = Path
            };
        }
    }
}