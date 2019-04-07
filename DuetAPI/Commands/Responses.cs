using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Base class for every response to a command request.
    /// An instance of this is returned when a regular <see cref="Command"/> has finished.
    /// </summary>
    /// <seealso cref="Response{T}"/>
    /// <seealso cref="ErrorResponse"/>
    public class BaseResponse
    {
        /// <summary>
        /// Indicates if the command could complete without a runtime error
        /// </summary>
        public bool Success { get; set; } = true;
    }

    /// <summary>
    /// Response of a <see cref="Command{T}"/>
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
    /// Response indicating a runtime exception during the internal processing of a command
    /// </summary>
    public sealed class ErrorResponse : BaseResponse
    {
        /// <summary>
        /// Name of the .NET exception
        /// </summary>
        public string ErrorType { get; set; }
        
        /// <summary>
        /// Message of the .NET exception
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a new response indicating a runtime error
        /// </summary>
        /// <param name="e">Exception to report</param>
        public ErrorResponse(Exception e)
        {
            Success = false;
            ErrorType = e.GetType().Name;
            ErrorMessage = e.Message;
        }
    }
}