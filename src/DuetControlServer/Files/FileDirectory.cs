namespace DuetControlServer.Files
{
    /// <summary>
    /// Shortcut for ToPhysicalAsync() to avoid multiple nested locks
    /// </summary>
    public enum FileDirectory
    {
        /// <summary>
        /// Filaments directory
        /// </summary>
        Filaments,

        /// <summary>
        /// Firmware directory
        /// </summary>
        Firmware,

        /// <summary>
        /// GCodes directory
        /// </summary>
        GCodes,

        /// <summary>
        /// Macros directory
        /// </summary>
        Macros,

        /// <summary>
        /// Menu directory
        /// </summary>
        Menu,

        /// <summary>
        /// System directory
        /// </summary>
        System,

        /// <summary>
        /// WWW directory
        /// </summary>
        Web
    }
}
