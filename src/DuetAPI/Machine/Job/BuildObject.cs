namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a detected build object
    /// </summary>
    public sealed class BuildObject : ModelObject
    {
        /// <summary>
        /// Name of the build object (if any)
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetPropertyValue(ref _name, value);
        }
        private string _name;

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
