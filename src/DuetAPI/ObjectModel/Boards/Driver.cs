namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a driver
    /// </summary>
    public sealed class Driver : ModelObject
    {
        /// <summary>
        /// Configured settings of the driver
        /// </summary>
        public DriverSettings Settings { get; } = new();
    }
}
