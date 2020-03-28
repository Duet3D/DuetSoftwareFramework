namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about configured calibration options
    /// </summary>
    public sealed class MoveCalibration : ModelObject
    {
        /// <summary>
        /// Final calibration results (for Delta calibration)
        /// </summary>
        public MoveCalibrationResults Final { get; } = new MoveCalibrationResults();

        /// <summary>
        /// Initial calibration results (for Delta calibration)
        /// </summary>
        public MoveCalibrationResults Initial { get; } = new MoveCalibrationResults();

        /// <summary>
        /// Number of factors used (for Delta calibration)
        /// </summary>
        public int NumFactors
        {
            get => _numFactors;
			set => SetPropertyValue(ref _numFactors, value);
        }
        private int _numFactors;
    }
}
