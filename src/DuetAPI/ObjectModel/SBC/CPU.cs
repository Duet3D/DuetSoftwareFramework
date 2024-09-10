namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the SBC's CPU
    /// </summary>
    public partial class CPU : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Average CPU load (0..100%) or null if unknown
        /// </summary>
        public float? AvgLoad
        {
            get => _avgLoad;
            set => SetPropertyValue(ref _avgLoad, value);
        }
        private float? _avgLoad;

        /// <summary>
        /// CPU hardware as reported by /proc/cpuinfo
        /// </summary>
        public string? Hardware
        {
            get => _hardware;
            set => SetPropertyValue(ref _hardware, value);
        }
        private string? _hardware;

        /// <summary>
        /// Number of CPU cores/threads (defaults to 1)
        /// </summary>
        public int NumCores
        {
            get => _numCores;
            set => SetPropertyValue(ref _numCores, value);
        }
        private int _numCores = 1;

        /// <summary>
        /// Current CPU temperature (in degC) or null if unknown
        /// </summary>
        public float? Temperature
        {
            get => _temperature;
            set => SetPropertyValue(ref _temperature, value);
        }
        float? _temperature;
    }
}
