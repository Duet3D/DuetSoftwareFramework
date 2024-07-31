namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about centre rotation as defined by G68
    /// </summary>
    public partial class MoveRotation : ModelObject
    {
        /// <summary>
        /// Angle of the centre rotatation (in deg)
        /// </summary>
        public float Angle
        {
            get => _angle;
            set => SetPropertyValue(ref _angle, value);
        }
        private float _angle;

        /// <summary>
        /// XY coordinates of the centre rotation
        /// </summary>
        public ModelCollection<float> Centre { get; } = [0F, 0F];
    }
}
