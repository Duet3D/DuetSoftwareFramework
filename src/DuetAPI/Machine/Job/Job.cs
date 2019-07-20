using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current file job (if any)
    /// </summary>
    public sealed class Job : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// Information about the file being processed
        /// </summary>
        public ParsedFileInfo File { get; private set; } = new ParsedFileInfo();
        
        /// <summary>
        /// Current position in the file being processed (in bytes or null)
        /// </summary>
        public long? FilePosition
        {
            get => _filePosition;
            set
            {
                if (_filePosition != value)
                {
                    _filePosition = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long? _filePosition;
        
        /// <summary>
        /// Name of the last file processed or null if none
        /// </summary>
        public string LastFileName
        {
            get => _lastFileName;
            set
            {
                if (_lastFileName != value)
                {
                    _lastFileName = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _lastFileName;
        
        /// <summary>
        /// Indicates if the last file processed was simulated
        /// </summary>
        public bool LastFileSimulated
        {
            get => _lastFileSimulated;
            set
            {
                if (_lastFileSimulated != value)
                {
                    _lastFileSimulated = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private bool _lastFileSimulated;

        /// <summary>
        /// Virtual amounts of extruded filament according to the G-code file (in mm)
        /// </summary>
        public ObservableCollection<float> ExtrudedRaw { get; } = new ObservableCollection<float>();
        
        /// <summary>
        /// Total duration of the current job (in s)
        /// </summary>
        public float? Duration
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
        private float? _duration;
        
        /// <summary>
        /// Number of the current layer or 0 if none has been started yet
        /// </summary>
        public int? Layer
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
        private int? _layer;
        
        /// <summary>
        /// Time elapsed since the beginning of the current layer (in s)
        /// </summary>
        public float? LayerTime
        {
            get => _layerTime;
            set
            {
                if (_layerTime != value)
                {
                    _layerTime = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _layerTime;
        
        /// <summary>
        /// Information about the past layers
        /// </summary>
        /// <seealso cref="Layer"/>
        public ObservableCollection<Layer> Layers { get; } = new ObservableCollection<Layer>();
        
        /// <summary>
        /// Time needed to heat up the heaters (in s)
        /// </summary>
        public float? WarmUpDuration
        {
            get => _warmUpDuration;
            set
            {
                if (_warmUpDuration != value)
                {
                    _warmUpDuration = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float? _warmUpDuration;
        
        /// <summary>
        /// Estimated times left
        /// </summary>
        public TimesLeft TimesLeft { get; private set; } = new TimesLeft();

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
            if (!(from is Job other))
            {
                throw new ArgumentException("Invalid type");
            }

            File.Assign(other.File);
            FilePosition = other.FilePosition;
            LastFileName = (other.LastFileName != null) ? string.Copy(other.LastFileName) : null;
            LastFileSimulated = other.LastFileSimulated;
            ListHelpers.SetList(ExtrudedRaw, other.ExtrudedRaw);
            Duration = other.Duration;
            Layer = other.Layer;
            LayerTime = other.LayerTime;
            ListHelpers.AssignList(Layers, other.Layers);
            WarmUpDuration = other.WarmUpDuration;
            TimesLeft.Assign(other.TimesLeft);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            Job clone = new Job
            {
                File = (ParsedFileInfo)File.Clone(),
                FilePosition = FilePosition,
                LastFileName = (LastFileName != null) ? string.Copy(LastFileName) : null,
                LastFileSimulated = LastFileSimulated,
                Duration = Duration,
                Layer = Layer,
                LayerTime = LayerTime,
                WarmUpDuration = WarmUpDuration,
                TimesLeft = (TimesLeft)TimesLeft.Clone()
            };

            ListHelpers.AddItems(clone.ExtrudedRaw, ExtrudedRaw);
            ListHelpers.CloneItems(clone.Layers, Layers);

            return clone;
        }
    }
}
