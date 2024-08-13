namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a detected build object
    /// </summary>
    public partial class BuildObject : ModelObject, IStaticModelObject
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
        public ModelCollection<float?> X { get; } = [];

        /// <summary>
        /// Y coordinates of the build object (in mm or null if not found)
        /// </summary>
        public ModelCollection<float?> Y { get; } = [];
    }
}
