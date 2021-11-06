namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Move segmentation parameters
    /// </summary>
    public sealed class MoveSegmentation : ModelObject
    {
        /// <summary>
        /// Number of segments per second
        /// </summary>
        public int SegmentsPerSec
        {
            get => _segmentsPerSec;
            set => SetPropertyValue(ref _segmentsPerSec, value);
        }
        private int _segmentsPerSec;

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
