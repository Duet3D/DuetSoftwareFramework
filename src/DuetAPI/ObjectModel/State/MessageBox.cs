using System.Collections.ObjectModel;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Information about the message box to show
    /// </summary>
    public partial class MessageBox : ModelObject, IStaticModelObject
    {
        /// <summary>
        /// Bitmap of the axis movement controls to show (indices)
        /// </summary>
        public long? AxisControls
        {
            get => _axisControls;
            set => SetPropertyValue(ref _axisControls, value);
        }
        private long? _axisControls;

        /// <summary>
        /// Indicates if a cancel button is supposed to be shown
        /// </summary>
        public bool CancelButton
        {
            get => _cancelButton;
            set => SetPropertyValue(ref _cancelButton, value);
        }
        private bool _cancelButton;

        /// <summary>
        /// List of possible choices (only for mode 4)
        /// </summary>
        public ObservableCollection<string>? Choices
        {
            get => _choices;
            set => SetPropertyValue(ref _choices, value);
        }
        private ObservableCollection<string>? _choices;

        /// <summary>
        /// Default value (only for modes >= 4)
        /// </summary>
        public object? Default
        {
            get => _default;
            set => SetPropertyValue(ref _default, value);
        }
        private object? _default;

        /// <summary>
        /// Maximum input value (only for modes >= 5)
        /// </summary>
        public float? Max
        {
            get => _max;
            set => SetPropertyValue(ref _max, value);
        }
        private float? _max;

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
        /// Minimum input value (only for modes >= 5)
        /// </summary>
        public float? Min
        {
            get => _min;
            set => SetPropertyValue(ref _min, value);
        }
        private float? _min;

        /// <summary>
        /// Mode of the message box to display
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