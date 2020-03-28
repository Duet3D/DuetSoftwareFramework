namespace DuetAPI.Machine
{
    /// <summary>
    /// Nonlinear extrusion parameters (see M592)
    /// </summary>
    public sealed class ExtruderNonlinear : ModelObject
    {
        /// <summary>
        /// A coefficient in the extrusion formula
        /// </summary>
        public float A
        {
            get => _a;
			set => SetPropertyValue(ref _a, value);
        }
        private float _a;

        /// <summary>
        /// B coefficient in the extrusion formula
        /// </summary>
        public float B
        {
            get => _b;
			set => SetPropertyValue(ref _b, value);
        }
        private float _b;

        /// <summary>
        /// Upper limit of the nonlinear extrusion compensation
        /// </summary>
        public float UpperLimit
        {
            get => _upperLimit;
			set => SetPropertyValue(ref _upperLimit, value);
        }
        private float _upperLimit = 0.2F;
    }
}