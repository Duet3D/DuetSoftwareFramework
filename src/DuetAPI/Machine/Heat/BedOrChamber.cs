using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a bed or chamber heater
    /// </summary>
    public sealed class BedOrChamber : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Active temperatures (in C)
        /// </summary>
        public ObservableCollection<float> Active { get; } = new ObservableCollection<float>();

        /// <summary>
        /// Standby temperatures (in C)
        /// </summary>
        public ObservableCollection<float> Standby { get; } = new ObservableCollection<float>();
        
        /// <summary>
        /// Name of the bed or chamber or null if unset
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
        /// Indices of the heaters controlled by this bed or chamber
        /// </summary>
        public ObservableCollection<int> Heaters { get; } = new ObservableCollection<int>();

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
            if (!(from is BedOrChamber other))
            {
                throw new ArgumentException("Invalid type");
            }

            ListHelpers.SetList(Active, other.Active);
            ListHelpers.SetList(Standby, other.Standby);
            Name = other.Name;
            ListHelpers.SetList(Heaters, other.Heaters);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            BedOrChamber clone = new BedOrChamber
            {
                Name = Name
            };

            ListHelpers.AddItems(clone.Active, Active);
            ListHelpers.AddItems(clone.Standby, Standby);
            ListHelpers.AddItems(clone.Heaters, Heaters);

            return clone;
        }
    }
}