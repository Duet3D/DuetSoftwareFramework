namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base kinematics class that provides the ability to level the bed using Z leadscrews
    /// </summary>
    public partial class ZLeadscrewKinematics : Kinematics
    {
        /// <summary>
        /// Parameters describing the tilt correction
        /// </summary>
        public TiltCorrection TiltCorrection { get; } = new TiltCorrection();
    }
}
