namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter interception mode
    /// Whenever a code is received, the connection must respond with one of
    /// - <cref see="DuetAPI.Commands.Cancel">Cancel</cref> to cancel the code
    /// - <cref see="DuetAPI.Commands.Ignore">Ignore</cref> to pass through the code without modifications
    /// - <cref see="DuetAPI.Commands.Resolve">Resolve</cref> to resolve the current code and to return a message
    /// In addition the interceptor may issue custom commands once a code has been received
    /// Do not attempt to perform commands before an intercepting code is received, else the order of
    /// command execution cannot be guaranteed
    /// </summary>
    public class InterceptInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public InterceptInitMessage()
        {
            Mode = ConnectionMode.Intercept;
        }
        
        /// <summary>
        /// Defines in what mode commands are supposed to be intercepted
        /// </summary>
        public InterceptionMode InterceptionMode { get; set; }
    }
}