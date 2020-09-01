namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class holding details about orthogonoal axis compensation parameters
    /// </summary>
    public sealed class Skew : ModelObject
    {
        /// <summary>
        /// Tangent of the skew angle for the XY axes
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
