namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Microstepping configuration
    /// </summary>
    public partial class Microstepping : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Indicates if the stepper driver uses interpolation
        /// </summary>
        public bool Interpolated
        {
            get => _interpolated;
            set => SetPropertyValue(ref _interpolated, value);
        }
        private bool _interpolated;

        /// <summary>
        /// Microsteps per full step
        /// </summary>
        public int Value
        {
            get => _value;
            set => SetPropertyValue(ref _value, value);
        }
        private int _value = 16;
    }
}
