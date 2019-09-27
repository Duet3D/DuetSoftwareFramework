using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a layer from a file being printed
    /// </summary>
    public sealed class Layer : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Duration of the layer (in s or null if unknown)
        /// </summary>
        public float Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _duration;

        /// <summary>
        /// Height of the layer (in mm or null if unknown)
        /// </summary>
        public float Height
        {
            get => _height;
            set
            {
                if (_height != value)
                {
                    _height = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _height;

        /// <summary>
        /// Actual amount of filament extruded during this layer (in mm)
        /// </summary>
        public List<float> Filament
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
        private List<float> _filament = new List<float>();

        /// <summary>
        /// Fraction of the file printed during this layer (0..1 or null if unknown)
        /// </summary>
        public float FractionPrinted
        {
            get => _fractionPrinted;
            set
            {
                if (_fractionPrinted != value)
                {
                    _fractionPrinted = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _fractionPrinted;

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
            if (!(from is Layer other))
            {
                throw new ArgumentException("Invalid type");
            }

            Duration = other.Duration;
            Height = other.Height;
            Filament = new List<float>(other.Filament);
            FractionPrinted = other.FractionPrinted;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>Clone of this instance</returns>
        public object Clone()
        {
            return new Layer
            {
                Duration = Duration,
                Height = Height,
                Filament = new List<float>(Filament),
                FractionPrinted = FractionPrinted
            };
        }
    }
}