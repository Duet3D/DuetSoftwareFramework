namespace DuetAPI.Commands
{
    /// <summary>
    /// Base class for every response to a Command
    /// </summary>
    public class BaseResponse
    {
        /// <summary>
        /// Indicates if the command could complete without a runtime error
        /// </summary>
        public bool Success { get; set; } = true;
    }

    /// <summary>
    /// Response of a command
    /// </summary>
    /// <typeparam name="T">Type of the response</typeparam>
    public sealed class Response<T> : BaseResponse
    {
        /// <summary>
        /// Result of the command
        /// </summary>
        public T Result { get; set; }

        /// <summary>
        /// Creates a new Response instance
        /// </summary>
        /// <param name="result"></param>
        public Response(T result)
        {
            Result = result;
        }
    }

    /// <summary>
    /// Response indicating an error occurred during the processing of a command
    /// </summary>
    public sealed class ErrorResponse : BaseResponse
    {
        /// <summary>
        /// Name of the .NET error
        /// </summary>
        public string ErrorType { get; set; }
        
        /// <summary>
        /// Error description of the .NET error
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a new response indicating a runtime error
        /// </summary>
        /// <param name="type">Name of the .NET error</param>
        /// <param name="message">Error description of the .NET error</param>
        public ErrorResponse(string type, string message)
        {
            Success = false;
            ErrorType = type;
            ErrorMessage = message;
        }
    }
}