namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a configured probe
    /// </summary>
    public partial class ProbeTouchMode : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Indicates if the touch probe is enabled
        /// </summary>
        public bool Active
        {
            get => _active;
			set => SetPropertyValue(ref _active, value);
        }
        private bool _active;

        /// <summary>
        /// Sensitivity of the touch probe
        /// </summary>
        public float Sensitivity
        {
            get => _sensitivity;
            set => SetPropertyValue(ref _sensitivity, value);
        }
        private float _sensitivity;

        /// <summary>
        /// Speed while probing in touch mode (in mm/s)
        /// </summary>
        public float Speed
        {
            get => _speed;
            set => SetPropertyValue(ref _speed, value);
        }
        private float _speed;

        /// <summary>
        /// Height of the trigger point of the touch probe (in mm)
        /// </summary>
        public float TriggerHeight
        {
            get => _triggerHeight;
            set => SetPropertyValue(ref _triggerHeight, value);
        }
        private float _triggerHeight;
    }
}