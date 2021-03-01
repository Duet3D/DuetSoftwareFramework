using System;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Estimations about the times left
    /// </summary>
    public sealed class TimesLeft : ModelObject
    {
        /// <summary>
        /// Time left based on filament consumption (in s or null)
        /// </summary>
        public int? Filament
        {
            get => _filament;
			set => SetPropertyValue(ref _filament, value);
        }
        private int? _filament;

        /// <summary>
        /// Time left based on file progress (in s or null)
        /// </summary>
        public int? File
        {
            get => _file;
			set => SetPropertyValue(ref _file, value);
        }
        private int? _file;
        
        /// <summary>
        /// Time left based on the layer progress (in s or null)
        /// </summary>
        [JsonIgnore]
        [Obsolete("No longer used, will always return null")]
        public int? Layer
        {
            get => _layer;
			set => SetPropertyValue(ref _layer, value);
        }
        private int? _layer;

        /// <summary>
        /// Time left based on the slicer reports (see M73, in s or null)
        /// </summary>
        public int? Slicer
        {
            get => _slicer;
            set => SetPropertyValue(ref _slicer, value);
        }
        private int? _slicer;
    }
}