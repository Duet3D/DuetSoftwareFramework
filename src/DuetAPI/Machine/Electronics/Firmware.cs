using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a firmware version
    /// </summary>
    public sealed class Firmware : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Name of the firmware
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _name;

        /// <summary>
        /// Version of the firmware
        /// </summary>
        public string Version
        {
            get => _version;
            set
            {
                if (_version != value)
                {
                    _version = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _version;

        /// <summary>
        /// Date of the firmware (i.e. when it was compiled)
        /// </summary>
        public string Date
        {
            get => _date;
            set
            {
                if (_date != value)
                {
                    _date = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _date;

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
            if (!(from is Firmware other))
            {
                throw new ArgumentException("Invalid type");
            }

            Name = (other.Name != null) ? string.Copy(other.Name) : null;
            Version = (other.Version != null) ? string.Copy(other.Version) : null;
            Date = (other.Date != null) ? string.Copy(other.Date) : null;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Firmware
            {
                Name = (Name != null) ? string.Copy(Name) : null,
                Version = (Version != null) ? string.Copy(Version) : null,
                Date = (Date != null) ? string.Copy(Date) : null
            };
        }
    }
}
