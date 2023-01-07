using DuetAPI;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Internal storage class for variable requests
    /// </summary>
    public class VariableRequest
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="createVariable">Whether the variable is supposed to be created</param>
        /// <param name="varName">Name of the variable</param>
        /// <param name="expression">Expression to evaluate</param>
        public VariableRequest(CodeChannel channel, bool createVariable, string varName, string? expression)
        {
            Channel = channel;
            CreateVariable = createVariable;
            VariableName = varName;
            Expression = expression;
        }

        /// <summary>
        /// Where the expression is evaluated
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Whether the variable is supposed to be created
        /// </summary>
        public bool CreateVariable { get; }

        /// <summary>
        /// Name of the variable
        /// </summary>
        public string VariableName { get; }

        /// <summary>
        /// Expression to set or null if the variable is supposed to be deleted
        /// </summary>
        public string? Expression { get; }

        /// <summary>
        /// Whether the request has been sent to the firmware
        /// </summary>
        public bool Written { get; set; }

        /// <summary>
        /// Internal TCS for the task
        /// </summary>
        private readonly TaskCompletionSource<object?> _tcs = new();

        /// <summary>
        /// Task that completes when the request has been fulfilled
        /// </summary>
        public Task<object?> Task => _tcs.Task;

        /// <summary>
        /// Set the result of the evaluated expression
        /// </summary>
        /// <param name="result">Result to set</param>
        public void SetResult(object? result) => _tcs.SetResult(result);

        /// <summary>
        /// Set the task to canceled
        /// </summary>
        public void SetCanceled() => _tcs.SetCanceled();

        /// <summary>
        /// Set an exception for the task
        /// </summary>
        /// <param name="exception">Exception to set</param>
        public void SetException(Exception exception) => _tcs.SetException(exception);
    }
}
