namespace DuetAPI.Machine
{
    /// <summary>
    /// Estimations about the times left
    /// </summary>
    public sealed class TimesLeft : ModelObject
    {
        /// <summary>
        /// Time left based on filament consumption (in s or null)
        /// </summary>
        public float? Filament
        {
            get => _filament;
			set => SetPropertyValue(ref _filament, value);
        }
        private float? _filament;

        /// <summary>
        /// Time left based on file progress (in s or null)
        /// </summary>
        public float? File
        {
            get => _file;
			set => SetPropertyValue(ref _file, value);
        }
        private float? _file;
        
        /// <summary>
        /// Time left based on the layer progress (in s or null)
        /// </summary>
        public float? Layer
        {
            get => _layer;
			set => SetPropertyValue(ref _layer, value);
        }
        private float? _layer;
    }
}