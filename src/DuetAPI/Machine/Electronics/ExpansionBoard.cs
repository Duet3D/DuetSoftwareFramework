using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Represents information about an attached expansion board
    /// </summary>
    public sealed class ExpansionBoard : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Short code name of the board
        /// </summary>
        public string ShortName
        {
            get => _shortName;
            set
            {
                if (_shortName != value)
                {
                    _shortName = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _shortName;

        /// <summary>
        /// Name of the attached expansion board
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
        /// Revision of the expansion board
        /// </summary>
        public string Revision
        {
            get => _revision;
            set
            {
                if (_revision != value)
                {
                    _revision = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _revision;
        
        /// <summary>
        /// Details about the firmware running on this expansion board
        /// </summary>
        public Firmware Firmware { get; private set; } = new Firmware();
        
        /// <summary>
        /// Set of the minimum, maximum and current input voltage (in V or null if unknown)
        /// </summary>
        public MinMaxCurrent<float?> VIn { get; private set; } = new MinMaxCurrent<float?>();
        
        /// <summary>
        /// Set of the minimum, maximum and current MCU temperature (in degC or null if unknown)
        /// </summary>
        public MinMaxCurrent<float?> McuTemp { get; private set; } = new MinMaxCurrent<float?>();
        
        /// <summary>
        /// How many heaters can be attached to this board
        /// </summary>
        public int? MaxHeaters
        {
            get => _maxHeaters;
            set
            {
                if (_maxHeaters != value)
                {
                    _maxHeaters = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _maxHeaters;
        
        /// <summary>
        /// How many drives can be attached to this board
        /// </summary>
        public int? MaxMotors
        {
            get => _maxMotors;
            set
            {
                if (_maxMotors != value)
                {
                    _maxMotors = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _maxMotors;

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
            if (!(from is ExpansionBoard other))
            {
                throw new ArgumentException("Invalid type");
            }

            ShortName = (other.ShortName != null) ? string.Copy(other.ShortName) : null;
            Name = (Name != null) ? string.Copy(other.Name) : null;
            Revision = (Revision != null) ? string.Copy(other.Revision) : null;
            Firmware.Assign(other.Firmware);
            VIn.Assign(other.VIn);
            McuTemp.Assign(other.McuTemp);
            MaxHeaters = other.MaxHeaters;
            MaxMotors = other.MaxMotors;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExpansionBoard
            {
                ShortName = (ShortName != null) ? string.Copy(ShortName) : null,
                Name = (Name != null) ? string.Copy(Name) : null,
                Revision = (Revision != null) ? string.Copy(Revision) : null,
                Firmware = (Firmware)Firmware.Clone(),
                VIn = (MinMaxCurrent<float?>)VIn.Clone(),
                McuTemp = (MinMaxCurrent<float?>)McuTemp.Clone(),
                MaxHeaters = MaxHeaters,
                MaxMotors = MaxMotors
            };
        }
    }
}
