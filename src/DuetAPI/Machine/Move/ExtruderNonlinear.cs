using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Nonlinear extrusion parameters (see M592)
    /// </summary>
    public sealed class ExtruderNonlinear : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// A coefficient in the extrusion formula
        /// </summary>
        public float A
        {
            get => _a;
            set
            {
                if (_a != value)
                {
                    _a = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _a;

        /// <summary>
        /// B coefficient in the extrusion formula
        /// </summary>
        public float B
        {
            get => _b;
            set
            {
                if (_b != value)
                {
                    _b = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _b;

        /// <summary>
        /// Upper limit of the nonlinear extrusion compensation
        /// </summary>
        public float UpperLimit
        {
            get => _upperLimit;
            set
            {
                if (_upperLimit != value)
                {
                    _upperLimit = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _upperLimit = 0.2F;

        /// <summary>
        /// Reserved for future use, for the temperature at which these values are valid (in degC)
        /// </summary>
        public float Temperature
        {
            get => _temperature;
            set
            {
                if (_temperature != value)
                {
                    _temperature = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _temperature;

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
            if (!(from is ExtruderNonlinear other))
            {
                throw new ArgumentException("Invalid type");
            }

            A = other.A;
            B = other.B;
            UpperLimit = other.UpperLimit;
            Temperature = other.Temperature;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExtruderNonlinear
            {
                A = A,
                B = B,
                UpperLimit = UpperLimit,
                Temperature = Temperature
            };
        }
    }
}