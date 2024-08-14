namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the current build
    /// </summary>
    public partial class Build : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Index of the current object being printed or -1 if unknown
        /// </summary>
        /// <remarks>
        /// This value may now be greater than the length of the job.build.objects array.
        /// This is because the size of job.build.objects is limited to conserve memory (to 20 on Duet 2 or 40 on Duet 3),
        /// whereas when M486 labelling is used, many more objects can be numbered and the first 64 can be cancelled individually
        /// </remarks>
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
        public StaticModelCollection<BuildObject> Objects { get; } = [];
    }
}
