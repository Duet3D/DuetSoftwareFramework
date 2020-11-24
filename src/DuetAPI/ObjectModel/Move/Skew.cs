namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class holding details about orthogonoal axis compensation parameters
    /// </summary>
    public sealed class Skew : ModelObject
    {
        /// <summary>
        /// Indicates if the <see cref="TanXY"/> value is applied to the X or Y axis value
        /// </summary>
        public bool CompensateXY
        {
            get => _compensateXY;
            set => SetPropertyValue(ref _compensateXY, value);
        }
        private bool _compensateXY;

        /// <summary>
        /// Tangent of the skew angle for the XY or YX axes
        /// </summary>
        public float TanXY
        {
            get => _tanXY;
            set => SetPropertyValue(ref _tanXY, value);
        }
        private float _tanXY;

        /// <summary>
        /// Tangent of the skew angle for the XZ axes
        /// </summary>
        public float TanXZ
        {
            get => _tanXZ;
            set => SetPropertyValue(ref _tanXZ, value);
        }
        private float _tanXZ;

        /// <summary>
        /// Tangent of the skew angle for the YZ axes
        /// </summary>
        public float TanYZ
        {
            get => _tanYZ;
            set => SetPropertyValue(ref _tanYZ, value);
        }
        private float _tanYZ;
    }
}
