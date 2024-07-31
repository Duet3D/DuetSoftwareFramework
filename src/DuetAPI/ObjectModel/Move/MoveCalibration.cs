namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about configured calibration options
    /// </summary>
    public partial class MoveCalibration : ModelObject
    {
        /// <summary>
        /// Final calibration results (for Delta calibration)
        /// </summary>
        public MoveDeviations Final { get; } = new MoveDeviations();

        /// <summary>
        /// Initial calibration results (for Delta calibration)
        /// </summary>
        public MoveDeviations Initial { get; } = new MoveDeviations();

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
