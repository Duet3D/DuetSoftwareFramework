namespace DuetAPI.Machine
{
    /// <summary>
    /// Supported modes of displaying a message box
    /// </summary>
    public enum MessageBoxMode
    {
        /// <summary>
        /// Display a message box without any buttons
        /// </summary>
        NoButtons = 0,

        /// <summary>
        /// Display a message box with only a Close button
        /// </summary>
        CloseOnly,

        /// <summary>
        /// Display a message box with only an Ok button which is supposed to send M292 when pressed
        /// </summary>
        OkOnly,

        /// <summary>
        /// Display a message box with an Ok button that sends M292 P0 or a cancel button that sends M292 P1 when clicked
        /// </summary>
        OkCancel
    }
}
