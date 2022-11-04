namespace DuetControlServer.Codes
{
    /// <summary>
    /// Enumeration of different stages in the code pipeline.
    /// The execution order of G/M/T-codes (in general) is from top to bottom.
    /// </summary>
    public enum PipelineStage
    {
        /// <summary>
        /// Code is about to start
        /// </summary>
        Start,

        /// <summary>
        /// Code is intercepted by third-party plugins
        /// </summary>
        /// <seealso cref="DuetAPI.Connection.InterceptionMode.Pre"/>
        Pre,

        /// <summary>
        /// Code is executed internally if applicable
        /// </summary>
        ProcessInternally,

        /// <summary>
        /// Code is intercepted by third-party plugins
        /// </summary>
        /// <seealso cref="DuetAPI.Connection.InterceptionMode.Post"/>
        Post,

        /// <summary>
        /// Code is processed by the firmware
        /// </summary>
        Firmware,

        /// <summary>
        /// Code has been executed (resolved or cancelled). It is intercepted by third-party plugins
        /// </summary>
        /// <seealso cref="DuetAPI.Connection.InterceptionMode.Executed"/>
        Executed
    }
}
