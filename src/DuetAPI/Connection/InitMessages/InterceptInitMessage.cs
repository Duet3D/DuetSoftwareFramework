using System.Collections.Generic;

namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter interception mode
    /// Whenever a code is received, the connection must respond with one of
    /// - <cref see="DuetAPI.Commands.Cancel">Cancel</cref> to cancel the code
    /// - <cref see="DuetAPI.Commands.Ignore">Ignore</cref> to pass through the code without modifications
    /// - <cref see="DuetAPI.Commands.Resolve">Resolve</cref> to resolve the current code and to return a message
    /// In addition the interceptor may issue custom commands once a code has been received
    /// </summary>
    /// <remarks>
    /// If this connection mode is used to implement new G/M/T-codes, always call the <see cref="Commands.Flush"/>
    /// command before further actions are started and make sure it returns <c>true</c> before the code is further
    /// processed. This step is mandatory to guarantee that the new code is executed when all other codes have finished
    /// and not when a code is being fed for the internal G-code buffer. If the flush command returns <c>false</c>, it
    /// is recommended to send <see cref="Commands.Cancel"/> to resolve the command. DCS follows the same pattern for
    /// internally processed codes, too.
    /// If a code from a macro file is intercepted, make sure to set the <see cref="Commands.CodeFlags.IsFromMacro"/>
    /// flag if new codes are inserted, else they will be started when the macro file(s) have finished. This step
    /// is obsolete if a <see cref="Commands.SimpleCode"/> is inserted.
    /// </remarks>
    public sealed class InterceptInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public InterceptInitMessage() => Mode = ConnectionMode.Intercept;
        
        /// <summary>
        /// Defines in what mode commands are supposed to be intercepted
        /// </summary>
        public InterceptionMode InterceptionMode { get; set; }

        /// <summary>
        /// List of channel where codes may be intercepted. If the list is empty, all available channels are used
        /// </summary>
        public List<CodeChannel> Channels { get; set; } = new List<CodeChannel>();

        /// <summary>
        /// List of G/M/T-codes to filter or Q for comments
        /// </summary>
        /// <remarks>
        /// This may only specify the code type and major/minor number (e.g. G1 or M105).
        /// Alternatively keyword types may be specified (e.g. if or elif).
        /// Asterisks are supported, too (e.g. T*)
        /// </remarks>
        public List<string> Filters { get; set; } = new List<string>();

        /// <summary>
        /// Defines if either regular or priority codes are supposed to be intercepted
        /// </summary>
        /// <seealso cref="Commands.CodeFlags.IsPrioritized"/>
        public bool PriortyCodes { get; set; }
    }
}