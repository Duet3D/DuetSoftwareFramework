using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Code bits to classify G/M/T-codes
    /// </summary>
    [Flags]
    public enum CodeFlags : int
    {
        /// <summary>
        /// Placeholder to indicate that no flags are set
        /// </summary>
        None = 0,

        /// <summary>
        /// Code execution finishes as soon as it is enqueued in the code queue
        /// </summary>
        /// <remarks>
        /// If codes are started asynchronously, code replies are normally reported via the object model.
        /// In order to keep track of code replies, an <see cref="Connection.ConnectionMode.Intercept"/> connection
        /// in <see cref="Connection.InterceptionMode.Executed"/> mode can be used
        /// </remarks>
        Asynchronous = 1,

        /// <summary>
        /// Code has been preprocessed (i.e. it has been processed by the DCS pre-side code interceptors)
        /// </summary>
        IsPreProcessed = 2,

        /// <summary>
        /// Code has been processed internally (if this is set the internal execution of a code is skipped)
        /// </summary>
        IsInternallyProcessed = 4096,

        /// <summary>
        /// Code has been postprocessed (i.e. it has been processed by the internal DCS code processor)
        /// </summary>
        IsPostProcessed = 4,

        /// <summary>
        /// Code originates from a macro file
        /// </summary>
        IsFromMacro = 8,

        /// <summary>
        /// Code originates from a system macro file (i.e. RRF requested it)
        /// </summary>
        IsNestedMacro = 16,

        /// <summary>
        /// Code comes from config.g or config.g.bak
        /// </summary>
        IsFromConfig = 32,

        /// <summary>
        /// Code comes from config-override.g
        /// </summary>
        IsFromConfigOverride = 64,

        /// <summary>
        /// Enforce absolute positioning via prefixed G53 code
        /// </summary>
        EnforceAbsolutePosition = 128,

        /// <summary>
        /// Execute this code as quickly as possible and skip codes that have the <see cref="Unbuffered"/> flag set
        /// </summary>
        /// <remarks>
        /// In order to execute this code as quickly as possible, DCS attempts to change the <see cref="Code.Channel"/> property
        /// to a a code channel that is completely idle. If this fails, a warning is logged. This flag should be only used for
        /// diagnostics and time-critical codes like M112/M122/M999
        /// </remarks>
        IsPrioritized = 256,

        /// <summary>
        /// Do NOT process another code on the same channel before this code has been fully executed.
        /// Note that priority codes may still override codes that have this flag set
        /// </summary>
        Unbuffered = 512,

        /// <summary>
        /// Indicates if this code was requested from the firmware
        /// </summary>
        IsFromFirmware = 1024,

        /// <summary>
        /// Indicates if this is the last code on the line
        /// </summary>
        IsLastCode = 2048
    }
}
