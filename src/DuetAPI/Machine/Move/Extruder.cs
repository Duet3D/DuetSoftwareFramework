namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an extruder drive
    /// </summary>
    public sealed class Extruder : ModelObject
    {
        /// <summary>
        /// Acceleration of this extruder (in mm/s^2)
        /// </summary>
        public float Acceleration
        {
            get => _acceleration;
			set => SetPropertyValue(ref _acceleration, value);
        }
        private float _acceleration = 500F;

        /// <summary>
        /// Motor current (in mA)
        /// </summary>
        public int Current
        {
            get => _current;
			set => SetPropertyValue(ref _current, value);
        }
        private int _current;

        /// <summary>
        /// Assigned driver
        /// </summary>
        public string Driver
        {
            get => _driver;
			set => SetPropertyValue(ref _driver, value);
        }
        private string _driver;

        /// <summary>
        /// Name of the currently loaded filament
        /// </summary>
        public string Filament
        {
            get => _filament;
			set => SetPropertyValue(ref _filament, value);
        }
        private string _filament = string.Empty;

        /// <summary>
        /// Extrusion factor to use (0..1 or greater)
        /// </summary>
        public float Factor
        {
            get => _factor;
			set => SetPropertyValue(ref _factor, value);
        }
        private float _factor = 1F;

        /// <summary>
        /// Motor jerk (in mm/s)
        /// </summary>
        public float Jerk
        {
            get => _jerk;
			set => SetPropertyValue(ref _jerk, value);
        }
        private float _jerk = 15F;

        /// <summary>
        /// Nonlinear extrusion parameters (see M592)
        /// </summary>
        public ExtruderNonlinear Nonlinear { get; private set; } = new ExtruderNonlinear();

        /// <summary>
        /// Extruder position
        /// </summary>
        public float Position
        {
            get => _position;
			set => SetPropertyValue(ref _position, value);
        }
        private float _position;

        /// <summary>
        /// Motor jerk (in mm/s)
        /// </summary>
        public float PressureAdvance
        {
            get => _pressureAdvance;
			set => SetPropertyValue(ref _pressureAdvance, value);
        }
        private float _pressureAdvance;

        /// <summary>
        /// Raw extruder position without extrusion factor applied
        /// </summary>
        public float RawPosition
        {
            get => _rawPosition;
			set => SetPropertyValue(ref _rawPosition, value);
        }
        private float _rawPosition;

        /// <summary>
        /// Maximum speed (in mm/s)
        /// </summary>
        public float Speed
        {
            get => _speed;
			set => SetPropertyValue(ref _speed, value);
        }
        private float _speed = 100F;
    }
}