namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Tool retraction parameters
    /// </summary>
    public partial class ToolRetraction : ModelObject
    {
        /// <summary>
        /// Amount of additional filament to extrude when undoing a retraction (in mm)
        /// </summary>
        public float ExtraRestart
        {
            get => _extraRestart;
			set => SetPropertyValue(ref _extraRestart, value);
        }
        private float _extraRestart;

        /// <summary>
        /// Retraction length (in mm)
        /// </summary>
        public float Length
        {
            get => _length;
			set => SetPropertyValue(ref _length, value);
        }
        private float _length;

        /// <summary>
        /// Retraction speed (in mm/s)
        /// </summary>
        public float Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private float _speed;

        /// <summary>
        /// Unretract speed (in mm/s)
        /// </summary>
        public float UnretractSpeed
        {
            get => _unretractSpeed;
			set => SetPropertyValue(ref _unretractSpeed, value);
        }
        private float _unretractSpeed;

        /// <summary>
        /// Amount of Z lift after doing a retraction (in mm)
        /// </summary>
        public float ZHop
        {
            get => _zHop;
			set => SetPropertyValue(ref _zHop, value);
        }
        private float _zHop;
    }
}
