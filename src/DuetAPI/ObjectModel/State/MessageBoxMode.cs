namespace DuetAPI.ObjectModel
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
        CloseOnly = 1,

        /// <summary>
        /// Display a message box with only an Ok button which is supposed to send M292 when pressed, blocking
        /// </summary>
        OkOnly = 2,

        /// <summary>
        /// Display a message box with an Ok button that sends M292 P0 or a cancel button that sends M292 P1 when clicked, blocking
        /// </summary>
        OkCancel = 3,

        /// <summary>
        /// Multiple choices, blocking
        /// </summary>
        MultipleChoice = 4,

        /// <summary>
        /// Integer value required, blocking 
        /// </summary>
        IntInput = 5,

        /// <summary>
        /// Floating-point value required, blocking
        /// </summary>
        FloatInput = 6,

        /// <summary>
        /// String value required, blocking
        /// </summary>
        StringInput = 7
    }
}
