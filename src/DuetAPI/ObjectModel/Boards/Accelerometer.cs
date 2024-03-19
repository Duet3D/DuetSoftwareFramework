namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// This represents an accelerometer
    /// </summary>
    public sealed class Accelerometer : ModelObject
    {
        /// <summary>
        /// Orientation of the accelerometer
        /// </summary>
        /// <remarks>
        /// See https://docs.duet3d.com/en/Duet3D_hardware/Accessories/Duet3D_Accelerometer#orientation for a list of orientations
        /// </remarks>
        public int Orientation
        {
            get => _orientation;
            set => SetPropertyValue(ref _orientation, value);
        }
        private int _orientation = 20;

        /// <summary>
        /// Number of collected data points in the last run or 0 if it failed
        /// </summary>
        public int Points
        {
            get => _points;
            set => SetPropertyValue(ref _points, value);
        }
        private int _points;

        /// <summary>
        /// Number of completed sampling runs
        /// </summary>
        public int Runs
        {
            get => _runs;
            set => SetPropertyValue(ref _runs, value);
        }
        private int _runs;
    }
}
