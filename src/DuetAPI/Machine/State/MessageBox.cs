namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the message box to show
    /// </summary>
    public sealed class MessageBox : ModelObject
    {
        /// <summary>
        /// Bitmap of the axis movement controls to show (indices)
        /// </summary>
        public long AxisControls
        {
            get => _axisControls;
            set => SetPropertyValue(ref _axisControls, value);
        }
        private long _axisControls;

        /// <summary>
        /// Content of the message box
        /// </summary>
        public string Message
        {
            get => _message;
			set => SetPropertyValue(ref _message, value);
        }
        private string _message = string.Empty;

        /// <summary>
        /// Mode of the message box to display or null if none is shown
        /// </summary>
        public MessageBoxMode Mode
        {
            get => _mode;
			set => SetPropertyValue(ref _mode, value);
        }
        private MessageBoxMode _mode = MessageBoxMode.OkOnly;

        /// <summary>
        /// Sequence number of the message box
        /// </summary>
        /// <remarks>
        /// This is increased whenever a new message box is supposed to be displayed
        /// </remarks>
        public int Seq
        {
            get => _seq;
			set => SetPropertyValue(ref _seq, value);
        }
        private int _seq = -1;

        /// <summary>
        /// Total timeout for this message box (in ms)
        /// </summary>
        public int Timeout
        {
            get => _timeout;
			set => SetPropertyValue(ref _timeout, value);
        }
        private int _timeout;

        /// <summary>
        /// Title of the message box
        /// </summary>
        public string Title
        {
            get => _title;
			set => SetPropertyValue(ref _title, value);
        }
        private string _title = string.Empty;
    }
}