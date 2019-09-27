using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a configured tool
    /// </summary>
    public sealed class Tool : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Number of the tool
        /// </summary>
        public int Number
        {
            get => _number;
            set
            {
                if (_number != value)
                {
                    _number = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _number;

        /// <summary>
        /// Active temperatures of the associated heaters (in C)
        /// </summary>
        public ObservableCollection<float> Active { get; } = new ObservableCollection<float>();

        /// <summary>
        /// Standby temperatures of the associated heaters (in C)
        /// </summary>
        public ObservableCollection<float> Standby { get; } = new ObservableCollection<float>();

        /// <summary>
        /// Name of the tool or null if unset
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
        /// Extruder drive index for resolving the tool filament (index or -1)
        /// </summary>
        public int FilamentExtruder
        {
            get => _filamentExtruder;
            set
            {
                if (_filamentExtruder != value)
                {
                    _filamentExtruder = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _filamentExtruder;
        
        /// <summary>
        /// Name of the currently loaded filament
        /// </summary>
        public string Filament
        {
            get => _filament;
            set
            {
                if (_filament != value)
                {
                    _filament = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _filament;

        /// <summary>
        /// List of associated fans (indices)
        /// </summary>
        public ObservableCollection<int> Fans { get; } = new ObservableCollection<int>();
        
        /// <summary>
        /// List of associated heaters (indices)
        /// </summary>
        public ObservableCollection<int> Heaters { get; } = new ObservableCollection<int>();
        
        /// <summary>
        /// Extruder drives of this tool
        /// </summary>
        public ObservableCollection<int> Extruders { get; } = new ObservableCollection<int>();

        /// <summary>
        /// Mix ratios of the associated extruder drives
        /// </summary>
        public ObservableCollection<float> Mix { get; } = new ObservableCollection<float>();
        
        /// <summary>
        /// Associated spindle (index or -1)
        /// </summary>
        public int Spindle
        {
            get => _spindle;
            set
            {
                if (_spindle != value)
                {
                    _spindle = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int _spindle = -1;
        
        /// <summary>
        /// Associated axes. At present only X and Y can be mapped per tool.
        /// </summary>
        /// <remarks>
        /// The order is the same as the visual axes, so by default the layout is
        /// [
        ///   [0],        // X
        ///   [1]         // Y
        /// ]
        /// Make sure to set each item individually so the change events are called
        /// </remarks>
        public ObservableCollection<int[]> Axes { get; } = new ObservableCollection<int[]>();

        /// <summary>
        /// Axis offsets (in mm)
        /// This list is in the same order as <see cref="Move.Axes"/>
        /// </summary>
        /// <seealso cref="Axis"/>
        public ObservableCollection<float> Offsets { get; } = new ObservableCollection<float>();

        /// <summary>
        /// Bitmap of the axes which were probed
        /// </summary>
        public int OffsetsProbed { get; set; }

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
            if (!(from is Tool other))
            {
                throw new ArgumentException("Invalid type");
            }

            Number = other.Number;
            ListHelpers.SetList(Active, other.Active);
            ListHelpers.SetList(Standby, other.Standby);
            Name = other.Name;
            Filament = other.Filament;
            ListHelpers.SetList(Fans, other.Fans);
            ListHelpers.SetList(Heaters, other.Heaters);
            ListHelpers.SetList(Extruders, other.Extruders);
            ListHelpers.SetList(Mix, other.Mix);
            Spindle = other.Spindle;
            ListHelpers.SetList(Axes, other.Axes);
            ListHelpers.SetList(Offsets, other.Offsets);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            Tool clone = new Tool
            {
                Number = Number,
                Name = Name,
                Filament = Filament,
                Spindle = Spindle
            };

            ListHelpers.AddItems(clone.Active, Active);
            ListHelpers.AddItems(clone.Standby, Standby);
            ListHelpers.AddItems(clone.Fans, Fans);
            ListHelpers.AddItems(clone.Heaters, Heaters);
            ListHelpers.AddItems(clone.Extruders, Extruders);
            ListHelpers.AddItems(clone.Mix, Mix);
            ListHelpers.CloneItems(clone.Axes, Axes);
            ListHelpers.AddItems(clone.Offsets, Offsets);

            return clone;
        }
    }
}