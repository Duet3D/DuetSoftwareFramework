using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an extruder drive
    /// </summary>
    public sealed class Extruder : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Drives of this extruder
        /// </summary>
        public ObservableCollection<int> Drives { get; } = new ObservableCollection<int>();

        /// <summary>
        /// Extrusion factor to use (1.0 equals 100%)
        /// </summary>
        public float Factor
        {
            get => _factor;
            set
            {
                if (_factor != value)
                {
                    _factor = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _factor = 1.0F;
        
        /// <summary>
        /// Nonlinear extrusion parameters (see M592)
        /// </summary>
        public ExtruderNonlinear Nonlinear { get; private set; } = new ExtruderNonlinear();

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
            if (!(from is Extruder other))
            {
                throw new ArgumentException("Invalid type");
            }

            ListHelpers.SetList(Drives, other.Drives);
            Factor = other.Factor;
            Nonlinear.Assign(other.Nonlinear);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Extruder clone = new Extruder
            {
                Factor = Factor,
                Nonlinear = (ExtruderNonlinear)Nonlinear.Clone()
            };

            ListHelpers.AddItems(clone.Drives, Drives);

            return clone;
        }
    }
}