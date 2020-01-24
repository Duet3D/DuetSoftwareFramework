using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the configured probe grid (see M557)
    /// </summary>
    /// <seealso cref="Heightmap"/>
    public class ProbeGrid : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// X start coordinate of the heightmap
        /// </summary>
        public float XMin
        {
            get => _xMin;
            set
            {
                if (value != _xMin)
                {
                    _xMin = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _xMin;

        /// <summary>
        /// X end coordinate of the heightmap
        /// </summary>
        public float XMax
        {
            get => _xMax;
            set
            {
                if (value != _xMax)
                {
                    _xMax = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _xMax;

        /// <summary>
        /// Spacing between the probe points in X direction
        /// </summary>
        public float XSpacing
        {
            get => _xSpacing;
            set
            {
                if (value != _xSpacing)
                {
                    _xSpacing = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _xSpacing;

        /// <summary>
        /// Y start coordinate of the heightmap
        /// </summary>
        public float YMin
        {
            get => _yMin;
            set
            {
                if (value != _yMin)
                {
                    _yMin = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _yMin;

        /// <summary>
        /// Y end coordinate of the heightmap
        /// </summary>
        public float YMax
        {
            get => _yMax;
            set
            {
                if (value != _yMax)
                {
                    _yMax = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _yMax;

        /// <summary>
        /// Spacing between the probe points in Y direction
        /// </summary>
        public float YSpacing
        {
            get => _ySpacing;
            set
            {
                if (value != _ySpacing)
                {
                    _ySpacing = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _ySpacing;

        /// <summary>
        /// Probing radius for delta kinematics
        /// </summary>
        public float Radius
        {
            get => _radius;
            set
            {
                if (value != _radius)
                {
                    _radius = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _radius;

        /// <summary>
        /// Spacing between the probe points for delta kinematics
        /// </summary>
        public float Spacing
        {
            get => _spacing;
            set
            {
                if (value != _spacing)
                {
                    _spacing = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _spacing;

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
            if (!(from is ProbeGrid other))
            {
                throw new ArgumentException("Invalid type");
            }

            XMin = other.XMin;
            XMax = other.XMax;
            XSpacing = other.XSpacing;
            YMin = other.YMin;
            YMax = other.YMax;
            YSpacing = other.YSpacing;
            Radius = other.Radius;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            ProbeGrid clone = new ProbeGrid
            {
                XMin = XMin,
                XMax = XMax,
                XSpacing = XSpacing,
                YMin = YMin,
                YMax = YMax,
                YSpacing = YSpacing,
                Radius = Radius
            };
            return clone;
        }
    }
}
