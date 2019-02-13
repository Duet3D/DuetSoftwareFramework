namespace DuetAPI.Commands
{
    // Perform a simple G/M/T-code
    // On completion a string is returned
    public class SimpleCode : Command<string>
    {
        // The code to execute
        public string Code { get; set; }
    }
}