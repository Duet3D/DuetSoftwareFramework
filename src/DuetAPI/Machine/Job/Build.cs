namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current build
    /// </summary>
    public sealed class Build : ModelObject
    {
        /// <summary>
        /// Index of the current object being printed or -1 if unknown
        /// </summary>
        public int CurrentObject
        {
            get => _currentObject;
            set => SetPropertyValue(ref _currentObject, value);
        }
        private int _currentObject = -1;

        /// <summary>
        /// Whether M486 names are being used
        /// </summary>
        public bool M486Names
        {
            get => _m486Names;
            set => SetPropertyValue(ref _m486Names, value);
        }
        private bool _m486Names;

        /// <summary>
        /// Whether M486 numbers are being used
        /// </summary>
        public bool M486Numbers
        {
            get => _m486Numbers;
            set => SetPropertyValue(ref _m486Numbers, value);
        }
        private bool _m486Numbers;

        /// <summary>
        /// List of detected build objects
        /// </summary>
        public ModelCollection<BuildObject> Objects { get; } = new ModelCollection<BuildObject>();
    }
}
