namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about sensors
    /// </summary>
    public partial class Sensors : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// List of analog sensors
        /// </summary>
        /// <seealso cref="AnalogSensor"/>
        public ModelCollection<AnalogSensor?> Analog { get; } = [];

        /// <summary>
        /// List of configured endstops
        /// </summary>
        /// <seealso cref="Endstop"/>
        public ModelCollection<Endstop?> Endstops { get; } = [];

        /// <summary>
        /// List of configured filament monitors
        /// </summary>
        /// <seealso cref="FilamentMonitor"/>
        public ModelCollection<FilamentMonitor?> FilamentMonitors { get; } = [];

        /// <summary>
        /// List of general-purpose input ports
        /// </summary>
        /// <seealso cref="GpInputPort"/>
        public ModelCollection<GpInputPort?> GpIn { get; } = [];

        /// <summary>
        /// List of configured probes
        /// </summary>
        /// <seealso cref="Probe"/>
        public ModelCollection<Probe?> Probes { get; } = [];
    }
}