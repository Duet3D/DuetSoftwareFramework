namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about sensors
    /// </summary>
    public sealed class Sensors : ModelObject
    {
        /// <summary>
        /// List of analog sensors
        /// </summary>
        /// <seealso cref="AnalogSensor"/>
        public ModelCollection<AnalogSensor?> Analog { get; } = new ModelCollection<AnalogSensor?>();

        /// <summary>
        /// List of configured endstops
        /// </summary>
        /// <seealso cref="Endstop"/>
        public ModelCollection<Endstop?> Endstops { get; } = new ModelCollection<Endstop?>();

        /// <summary>
        /// List of configured filament monitors
        /// </summary>
        /// <seealso cref="FilamentMonitor"/>
        public ModelCollection<FilamentMonitor?> FilamentMonitors { get; } = new ModelCollection<FilamentMonitor?>();

        /// <summary>
        /// List of general-purpose input ports
        /// </summary>
        /// <seealso cref="GpInputPort"/>
        public ModelCollection<GpInputPort?> GpIn { get; } = new ModelCollection<GpInputPort?>();

        /// <summary>
        /// List of configured probes
        /// </summary>
        /// <seealso cref="Probe"/>
        public ModelCollection<Probe?> Probes { get; } = new ModelCollection<Probe?>();
    }
}