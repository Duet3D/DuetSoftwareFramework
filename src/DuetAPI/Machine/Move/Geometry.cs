using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the configured geometry
    /// </summary>
    public sealed class Geometry : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Currently configured geometry type
        /// </summary>
        public GeometryType Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private GeometryType _type = GeometryType.Unknown;

        /// <summary>
        /// Hangprinter A, B, C, Dz anchors (10 values)
        /// </summary>
        public ObservableCollection<float> Anchors { get; } = new ObservableCollection<float>() { 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F };

        /// <summary>
        /// Print radius for Hangprinter and Delta geometries in mm
        /// </summary>
        public float PrintRadius
        {
            get => _printRadius;
            set
            {
                if (_printRadius != value)
                {
                    _printRadius = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _printRadius;

        /// <summary>
        /// Delta diagonals
        /// </summary>
        public ObservableCollection<float> Diagonals { get; } = new ObservableCollection<float>() { 0.0F, 0.0F, 0.0F };

        /// <summary>
        /// Delta radius in mm
        /// </summary>
        public float Radius
        {
            get => _radius;
            set
            {
                if (_radius != value)
                {
                    _radius = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _radius;

        /// <summary>
        /// Homed height of a delta printer in mm
        /// </summary>
        public float HomedHeight
        {
            get => _homedHeight;
            set
            {
                if (_homedHeight != value)
                {
                    _homedHeight = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _homedHeight;

        /// <summary>
        /// ABC angle corrections for delta geometries
        /// </summary>
        public ObservableCollection<float> AngleCorrections { get; } = new ObservableCollection<float>() { 0.0F, 0.0F, 0.0F };

        /// <summary>
        /// Endstop adjustments of the XYZ axes in mm
        /// </summary>
        public ObservableCollection<float> EndstopAdjustments { get; } = new ObservableCollection<float>() { 0.0F, 0.0F, 0.0F };

        /// <summary>
        /// Tilt values of the XY axes
        /// </summary>
        public ObservableCollection<float> Tilt { get; } = new ObservableCollection<float>() { 0.0F, 0.0F };

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
            if (!(from is Geometry other))
            {
                throw new ArgumentException("Invalid type");
            }

            Type = other.Type;
            ListHelpers.SetList(Anchors, other.Anchors);
            PrintRadius = other.PrintRadius;
            ListHelpers.SetList(Diagonals, other.Diagonals);
            Radius = other.Radius;
            HomedHeight = other.HomedHeight;
            ListHelpers.SetList(AngleCorrections, other.AngleCorrections);
            ListHelpers.SetList(EndstopAdjustments, other.EndstopAdjustments);
            ListHelpers.SetList(Tilt, other.Tilt);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Geometry clone = new Geometry
            {
                Type = Type,
                PrintRadius = PrintRadius,
                Radius = Radius,
                HomedHeight = HomedHeight
            };

            ListHelpers.SetList(clone.Anchors, Anchors);
            ListHelpers.SetList(clone.Diagonals, Diagonals);
            ListHelpers.SetList(clone.AngleCorrections, AngleCorrections);
            ListHelpers.SetList(clone.EndstopAdjustments, EndstopAdjustments);
            ListHelpers.SetList(clone.Tilt, Tilt);

            return clone;
        }
    }
}
