namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Coordinates of a keep-out zone
    /// </summary>
    public sealed class KeepoutZoneCoordinates : ModelObject
    {
        /// <summary>
        /// Minimum axis coordinate
        /// </summary>
        public float Min
        {
            get => _min;
            set => SetPropertyValue(ref _min, value);
        }
        private float _min = 0F;

        /// <summary>
        /// Maximum axis coordinate
        /// </summary>
        public float Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private float _max = 0F;
    }

    /// <summary>
    /// Information about a configured keep-out zone
    /// </summary>
    public sealed class KeepoutZone : ModelObject
    {
        /// <summary>
        /// Indicates if this keep-out zone is enabled
        /// </summary>
        public bool Active
        {
            get => _active;
			set => SetPropertyValue(ref _active, value);
        }
        private bool _active = true;

        /// <summary>
        /// Minimum and maximum coordinates of this zone
        /// </summary>
        public ModelCollection<KeepoutZoneCoordinates> Coords { get; } = [];
    }
}