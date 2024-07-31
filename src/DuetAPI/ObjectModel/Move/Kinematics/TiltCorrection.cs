namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Tilt correction parameters for Z leadscrew compensation
    /// </summary>
    public partial class TiltCorrection : ModelObject
    {
        /// <summary>
        /// Correction factor
        /// </summary>
        public float CorrectionFactor
        {
            get => _correctionFactor;
            set => SetPropertyValue(ref _correctionFactor, value);
        }
        private float _correctionFactor;

        /// <summary>
        /// Last corrections (in mm)
        /// </summary>
        public ModelCollection<float> LastCorrections { get; } = [];

        /// <summary>
        /// Maximum Z correction (in mm)
        /// </summary>
        public float MaxCorrection
        {
            get => _maxCorrection;
            set => SetPropertyValue(ref _maxCorrection, value);
        }
        private float _maxCorrection;

        /// <summary>
        /// Pitch of the Z leadscrews (in mm)
        /// </summary>
        public float ScrewPitch
        {
            get => _screwPitch;
            set => SetPropertyValue(ref _screwPitch, value);
        }
        private float _screwPitch;

        /// <summary>
        /// X positions of the leadscrews (in mm)
        /// </summary>
        public ModelCollection<float> ScrewX { get; } = [];

        /// <summary>
        /// Y positions of the leadscrews (in mm)
        /// </summary>
        public ModelCollection<float> ScrewY { get; } = [];
    }
}
