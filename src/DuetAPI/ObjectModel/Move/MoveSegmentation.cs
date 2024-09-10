namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Move segmentation parameters
    /// </summary>
    public partial class MoveSegmentation : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Number of segments per second
        /// </summary>
        public float SegmentsPerSec
        {
            get => _segmentsPerSec;
            set => SetPropertyValue(ref _segmentsPerSec, value);
        }
        private float _segmentsPerSec;

        /// <summary>
        /// Minimum length of a segment (in mm)
        /// </summary>
        public float MinSegmentLength
        {
            get => _minSegmentLength;
            set => SetPropertyValue(ref _minSegmentLength, value);
        }
        private float _minSegmentLength;
    }
}
