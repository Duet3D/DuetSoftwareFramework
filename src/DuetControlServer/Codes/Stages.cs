namespace DuetControlServer.Codes
{
    /// <summary>
    /// Enumeration of different stages in the code pipeline
    /// </summary>
    public enum PipelineStage
    {
        /// <summary>
        /// Code is about to start
        /// </summary>
        Start,

        /// <summary>
        /// Code is intercepted by third-party plugins (pre stage)
        /// </summary>
        Pre,

        /// <summary>
        /// Code is executed internally if applicable
        /// </summary>
        ProcessInternally,

        /// <summary>
        /// Code is intercepted by third-party plugins (post stage)
        /// </summary>
        Post,

        /// <summary>
        /// Code is processed by the firmware
        /// </summary>
        Firmware,

        /// <summary>
        /// Code has been executed (resolved or cancelled). It is intercepted by third-party plugins (executed stage)
        /// </summary>
        Executed
    }
}
