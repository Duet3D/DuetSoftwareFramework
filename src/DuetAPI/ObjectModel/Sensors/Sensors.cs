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
        public StaticModelCollection<AnalogSensor?> Analog { get; } = [];

        /// <summary>
        /// List of configured endstops
        /// </summary>
        /// <seealso cref="Endstop"/>
        public StaticModelCollection<Endstop?> Endstops { get; } = [];

        /// <summary>
        /// List of configured filament monitors
        /// </summary>
        /// <seealso cref="FilamentMonitor"/>
        public DynamicModelCollection<FilamentMonitor?> FilamentMonitors { get; } = [];

        /// <summary>
        /// List of general-purpose input ports
        /// </summary>
        /// <seealso cref="GpInputPort"/>
        public StaticModelCollection<GpInputPort?> GpIn { get; } = [];

        /// <summary>
        /// List of configured probes
        /// </summary>
        /// <seealso cref="Probe"/>
        public StaticModelCollection<Probe?> Probes { get; } = [];
    }
}