namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Type of a generic message
    /// </summary>
    public enum MessageType : int
    {
        /// <summary>
        /// This is a success message
        /// </summary>
        Success = 0,

        /// <summary>
        /// This is a warning message
        /// </summary>
        Warning,

        /// <summary>
        /// This is an error message
        /// </summary>
        Error
    }
}
