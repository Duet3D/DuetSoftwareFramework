namespace DuetAPI.Commands
{
    /// <summary>
    /// Perform a simple G/M/T-code.
    /// Internally the code passed is populated as a full <see cref="Code"/> instance and on completion
    /// its CodeResult is transformed back into a basic string. This is useful for minimal extensions
    /// that do not require granular control of the code details
    /// </summary>
    public class SimpleCode : Command<string>
    {
        // The code to execute
        public string Code { get; set; }
    }
}