using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Estimations about the times left
    /// </summary>
    public sealed class TimesLeft : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Time left based on file progress (in s or null)
        /// </summary>
        public float? File
        {
            get => _file;
            set
            {
                if (_file != value)
                {
                    _file = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _file;
        
        /// <summary>
        /// Time left based on filament consumption (in s or null)
        /// </summary>
        public float? Filament
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
        private float? _filament;
        
        /// <summary>
        /// Time left based on the layer progress (in s or null)
        /// </summary>
        public float? Layer
        {
            get => _layer;
            set
            {
                if (_layer != value)
                {
                    _layer = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _layer;

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
            if (!(from is TimesLeft other))
            {
                throw new ArgumentException("Invalid type");
            }

            File = other.File;
            Filament = other.Filament;
            Layer = other.Layer;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new TimesLeft
            {
                File = File,
                Filament = Filament,
                Layer = Layer
            };
        }
    }
}