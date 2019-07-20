using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the heat subsystem
    /// </summary>
    public sealed class Heat : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// List of configured beds
        /// </summary>
        /// <remarks>
        /// This may contain null items
        /// </remarks>
        /// <seealso cref="BedOrChamber"/>
        public ObservableCollection<BedOrChamber> Beds { get; } = new ObservableCollection<BedOrChamber>();
        
        /// <summary>
        /// List of configured chambers 
        /// </summary>
        /// <remarks>
        /// This may contain null items
        /// </remarks>
        /// <seealso cref="BedOrChamber"/>
        public ObservableCollection<BedOrChamber> Chambers { get; } = new ObservableCollection<BedOrChamber>();
        
        /// <summary>
        /// Minimum required temperature for extrusion moves (in C)
        /// </summary>
        public float ColdExtrudeTemperature
        {
            get => _coldExtrudeTemperature;
            set
            {
                if (_coldExtrudeTemperature != value)
                {
                    _coldExtrudeTemperature = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _coldExtrudeTemperature = 160.0F;
        
        /// <summary>
        /// Minimum required temperature for retraction moves (in C)
        /// </summary>
        public float ColdRetractTemperature
        {
            get => _coldRetractTemperature;
            set
            {
                if (_coldRetractTemperature != value)
                {
                    _coldRetractTemperature = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _coldRetractTemperature = 90.0F;
        
        /// <summary>
        /// List of configured extra heaters
        /// </summary>
        /// <seealso cref="ExtraHeater"/>
        public ObservableCollection<ExtraHeater> Extra { get; } = new ObservableCollection<ExtraHeater>();
        
        /// <summary>
        /// List of configured heaters
        /// </summary>
        /// <seealso cref="Heater"/>
        public ObservableCollection<Heater> Heaters { get; } = new ObservableCollection<Heater>();

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
            if (!(from is Heat other))
            {
                throw new ArgumentException("Invalid type");
            }

            ListHelpers.AssignList(Beds, other.Beds);
            ListHelpers.AssignList(Chambers, other.Chambers);
            ColdExtrudeTemperature = other.ColdExtrudeTemperature;
            ColdRetractTemperature = other.ColdRetractTemperature;
            ListHelpers.AssignList(Extra, other.Extra);
            ListHelpers.AssignList(Heaters, other.Heaters);
        }

        /// <summary>
        /// Creates a copy of this instance
        /// </summary>
        /// <returns>A copy of this instance</returns>
        public object Clone()
        {
            Heat clone = new Heat
            {
                ColdExtrudeTemperature = ColdExtrudeTemperature,
                ColdRetractTemperature = ColdRetractTemperature
            };

            ListHelpers.CloneItems(clone.Beds, Beds);
            ListHelpers.CloneItems(clone.Chambers, Chambers);
            ListHelpers.CloneItems(clone.Extra, Extra);
            ListHelpers.CloneItems(clone.Heaters, Heaters);

            return clone;
        }
    }
}
