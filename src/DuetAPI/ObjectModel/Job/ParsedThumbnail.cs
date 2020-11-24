namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Holds image parsed out of G-code files
    /// </summary>
    public sealed class ParsedThumbnail : ModelObject
    {
        /// <summary>
        /// Width of thumbnail
        /// </summary>
        public int Width
        {
            get => _width;
            set => SetPropertyValue(ref _width, value);
        }
        private int _width;
        
        /// <summary>
        /// Height of thumbnail
        /// </summary>
        public int Height
        {
            get => _height;
            set => SetPropertyValue(ref _height, value);
        }
        private int _height;
        
        /// <summary>
        /// base64 encoded thumbnail
        /// </summary>
        public string EncodedImage
        {
            get => _encodedImage;
            set => SetPropertyValue(ref _encodedImage, value);
        }
        private string _encodedImage;
    }
}
