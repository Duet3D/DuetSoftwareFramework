namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about a thumbnail from a G-code file
    /// </summary>
    public sealed class ThumbnailInfo : ModelObject
    {
        /// <summary>
        /// Base64-encoded thumbnail or null if invalid or not requested
        /// </summary>
        /// <remarks>
        /// This property is not provided by RepRapFirmware fileinfo results and it may be null if no thumbnail content is requested
        /// </remarks>
        public string Data
        {
            get => _data;
            set => SetPropertyValue(ref _data, value);
        }
        private string _data;

        /// <summary>
        /// Format of this thumbnail
        /// </summary>
        public ThumbnailInfoFormat Format
        {
            get => _format;
            set => SetPropertyValue(ref _format, value);
        }
        private ThumbnailInfoFormat _format;

        /// <summary>
        /// Height of this thumbnail
        /// </summary>
        public int Height
        {
            get => _height;
            set => SetPropertyValue(ref _height, value);
        }
        private int _height;

        /// <summary>
        /// File offset of this thumbnail
        /// </summary>
        public long Offset
        {
            get => _offset;
            set => SetPropertyValue(ref _offset, value);
        }
        private long _offset;

        /// <summary>
        /// Size of this thumbnail
        /// </summary>
        public int Size
        {
            get => _size;
            set => SetPropertyValue(ref _size, value);
        }
        private int _size;

        /// <summary>
        /// Width of this thumbnail
        /// </summary>
        public int Width
        {
            get => _width;
            set => SetPropertyValue(ref _width, value);
        }
        private int _width;
    }
}
