namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class providing information about a connected direct-connect display
    /// </summary>
    public sealed class DirectDisplay : ModelObject
    {
        /// <summary>
        /// Encoder of this screen or null if none
        /// </summary>
        public DirectDisplayEncoder? Encoder
        {
            get => _encoder;
            set => SetPropertyValue(ref _encoder, value);
        }
        private DirectDisplayEncoder? _encoder = new();

        /// <summary>
        /// Screen information
        /// </summary>
        public DirectDisplayScreen Screen
        {
            get => _screen;
            set => SetPropertyValue(ref _screen, value);
        }
        private DirectDisplayScreen _screen = new();
    }
}
