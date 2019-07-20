using DuetAPI.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DuetAPI
{
    /// <summary>
    /// Holds information about a parsed G-code file
    /// </summary>
    public sealed class ParsedFileInfo : IAssignable, ICloneable, INotifyPropertyChanged
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
        /// The filename of the G-code file
        /// </summary>
        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _fileName;
        
        /// <summary>
        /// Size of the file
        /// </summary>
        public long Size
        {
            get => _size;
            set
            {
                if (_size != value)
                {
                    _size = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long _size;
        
        /// <summary>
        /// Date and time of the last modification or null if none is set 
        /// </summary>
        public DateTime? LastModified
        {
            get => _lastModified;
            set
            {
                if (_lastModified != value)
                {
                    _lastModified = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private DateTime? _lastModified;
        
        /// <summary>
        /// Build height of the G-code job or 0 if not found (in mm)
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
        /// Height of the first layer or 0 if not found (in mm)
        /// </summary>
        public float FirstLayerHeight
        {
            get => _firstLayerHeight;
            set
            {
                if (_firstLayerHeight != value)
                {
                    _firstLayerHeight = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _firstLayerHeight;
        
        /// <summary>
        /// Height of each other layer or 0 if not found (in mm)
        /// </summary>
        public float LayerHeight
        {
            get => _layerHeight;
            set
            {
                if (_layerHeight != value)
                {
                    _layerHeight = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private float _layerHeight;
        
        /// <summary>
        /// Number of total layers or null if unknown
        /// </summary>
        public int? NumLayers
        {
            get => _numLayers;
            set
            {
                if (_numLayers != value)
                {
                    _numLayers = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private int? _numLayers;

        /// <summary>
        /// Filament consumption per extruder drive (in mm)
        /// </summary>
        public ObservableCollection<float> Filament { get; } = new ObservableCollection<float>();

        /// <summary>
        /// Name of the application that generated this file
        /// </summary>
        public string GeneratedBy
        {
            get => _generatedBy;
            set
            {
                if (_generatedBy != value)
                {
                    _generatedBy = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private string _generatedBy = "";
        
        /// <summary>
        /// Estimated print time (in s)
        /// </summary>
        public long PrintTime
        {
            get => _printTime;
            set
            {
                if (_printTime != value)
                {
                    _printTime = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long _printTime;
        
        /// <summary>
        /// Estimated print time from G-code simulation (in s)
        /// </summary>
        public long SimulatedTime
        {
            get => _simulatedTime;
            set
            {
                if (_simulatedTime != value)
                {
                    _simulatedTime = value;
                    NotifyPropertyChanged();
                }
            }
        }
        private long _simulatedTime;

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
            if (!(from is ParsedFileInfo other))
            {
                throw new ArgumentException("Invalid type");
            }

            FileName = (other.FileName != null) ? string.Copy(other.FileName) : null;
            Size = other.Size;
            LastModified = other.LastModified;
            Height = other.Height;
            FirstLayerHeight = other.FirstLayerHeight;
            LayerHeight = other.LayerHeight;
            NumLayers = other.NumLayers;
            GeneratedBy = string.Copy(other.GeneratedBy);
            PrintTime = other.PrintTime;
            SimulatedTime = other.SimulatedTime;
            ListHelpers.SetList(Filament, other.Filament);
        }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            ParsedFileInfo clone = new ParsedFileInfo
            {
                FileName = (FileName != null) ? string.Copy(FileName) : null,
                Size = Size,
                LastModified = LastModified,
                Height = Height,
                FirstLayerHeight = FirstLayerHeight,
                LayerHeight = LayerHeight,
                NumLayers = NumLayers,
                GeneratedBy = string.Copy(GeneratedBy),
                PrintTime = PrintTime,
                SimulatedTime = SimulatedTime
            };

            ListHelpers.AddItems(clone.Filament, Filament);

            return clone;
        }
    }
}