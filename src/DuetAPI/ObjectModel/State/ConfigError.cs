namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Details about the first error in config.g
    /// </summary>
    public sealed class ConfigError : ModelObject
    {
        /// <summary>
        /// Filename of the macro where the error occurred
        /// </summary>
        public string File
        {
            get => _file;
			set => SetPropertyValue(ref _file, value);
        }
        private string _file = string.Empty;

        /// <summary>
        /// Line number of the error
        /// </summary>
        public int Line
        {
            get => _line;
			set => SetPropertyValue(ref _line, value);
        }
        private int _line;

        /// <summary>
        /// Message of the error
        /// </summary>
        public string Message
        {
            get => _message;
            set => SetPropertyValue(ref _message, value);
        }
        private string _message = string.Empty;
    }
}
