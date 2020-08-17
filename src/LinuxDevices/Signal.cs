namespace LinuxApi
{
    /// <summary>
    /// Helper class to send signals to other processes
    /// </summary>
    public enum Signal
    {
        /// <summary>
        /// Signal to ask a process for graceful termination
        /// </summary>
        SIGTERM = 15
    }
}
