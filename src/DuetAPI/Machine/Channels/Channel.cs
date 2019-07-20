using DuetAPI.Utility;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a G/M/T-code channel
    /// </summary>
    public sealed class Channel : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Emulation used on this channel
        /// </summary>
        public Compatibility Compatibility
        {
            get => _compatibility;
            set
            {
                if (_compatibility != value)
                {
                    _compatibility = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private Compatibility _compatibility = Compatibility.RepRapFirmware;

        /// <summary>
        /// Current feedrate in mm/s
        /// </summary>
        public float Feedrate
        {
            get => _feedrate;
            set
            {
                if (_feedrate != value)
                {
                    _feedrate = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _feedrate = 50.0F;
        
        /// <summary>
        /// Whether relative extrusion is being used
        /// </summary>
        public bool RelativeExtrusion
        {
            get => _relativeExtrusion;
            set
            {
                if (_relativeExtrusion != value)
                {
                    _relativeExtrusion = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _relativeExtrusion = true;

        /// <summary>
        /// Whether volumetric extrusion is being used
        /// </summary>
        public bool VolumetricExtrusion
        {
            get => _volumetricExtrusion;
            set
            {
                if (_volumetricExtrusion != value)
                {
                    _volumetricExtrusion = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _volumetricExtrusion = false;

        /// <summary>
        /// Whether relative positioning is being used
        /// </summary>
        public bool RelativePositioning
        {
            get => _relativePositioning;
            set
            {
                if (_relativePositioning != value)
                {
                    _relativePositioning = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _relativePositioning = false;

        /// <summary>
        /// Whether inches are being used instead of mm
        /// </summary>
        public bool UsingInches
        {
            get => _usingInches;
            set
            {
                if (_usingInches != value)
                {
                    _usingInches = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _usingInches = false;

        /// <summary>
        /// Depth of the stack
        /// </summary>
        public byte StackDepth
        {
            get => _stackDepth;
            set
            {
                if (_stackDepth != value)
                {
                    _stackDepth = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private byte _stackDepth = 0;

        /// <summary>
        /// Number of the current line
        /// </summary>
        public long LineNumber
        {
            get => _lineNumber;
            set
            {
                if (_lineNumber != value)
                {
                    _lineNumber = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long _lineNumber = 0;

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
            if (!(from is Channel other))
            {
                throw new ArgumentException("Invalid type");
            }

            Compatibility = other.Compatibility;
            Feedrate = other.Feedrate;
            RelativeExtrusion = other.RelativeExtrusion;
            VolumetricExtrusion = other.VolumetricExtrusion;
            RelativePositioning = other.RelativePositioning;
            UsingInches = other.UsingInches;
            StackDepth = other.StackDepth;
            LineNumber = other.LineNumber;
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Channel
            {
                Compatibility = Compatibility,
                Feedrate = Feedrate,
                RelativeExtrusion = RelativeExtrusion,
                VolumetricExtrusion = VolumetricExtrusion,
                RelativePositioning = RelativePositioning,
                UsingInches = UsingInches,
                StackDepth = StackDepth,
                LineNumber = LineNumber
            };
        }
    }
}
