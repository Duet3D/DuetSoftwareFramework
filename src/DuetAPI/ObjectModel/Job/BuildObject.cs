namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a detected build object
    /// </summary>
    public sealed class BuildObject : ModelObject
    {
        /// <summary>
        /// Indicates if this build object is cancelled
        /// </summary>
        public bool Cancelled
        {
            get => _cancelled;
            set => SetPropertyValue(ref _cancelled, value);
        }
        private bool _cancelled;

        /// <summary>
        /// Name of the build object (if any)
        /// </summary>
        public string? Name
        {
            get => _name;
            set => SetPropertyValue(ref _name, value);
        }
        private string? _name;

        /// <summary>
        /// X coordinates of the build object (in mm or null if not found)
        /// </summary>
        public ModelCollection<float?> X { get; } = new ModelCollection<float?>();

        /// <summary>
        /// Y coordinates of the build object (in mm or null if not found)
        /// </summary>
        public ModelCollection<float?> Y { get; } = new ModelCollection<float?>();
    }
}
