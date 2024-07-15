using DuetAPI;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Internal storage class for expression evaluation requests
    /// </summary>
    /// <param name="channel">Where to evaluate the expression</param>
    /// <param name="expression">Expression to evaluate</param>
    public class EvaluateExpressionRequest(CodeChannel channel, string expression)
    {
        /// <summary>
        /// Where the expression is evaluated
        /// </summary>
        public CodeChannel Channel { get; } = channel;

        /// <summary>
        /// Expression to evaluate
        /// </summary>
        public string Expression { get; } = expression;

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
        public void SetResult(object? result) => _tcs.TrySetResult(result);

        /// <summary>
        /// Set the task to canceled
        /// </summary>
        public void SetCanceled() => _tcs.TrySetCanceled();

        /// <summary>
        /// Set an exception for the task
        /// </summary>
        /// <param name="exception">Exception to set</param>
        public void SetException(Exception exception) => _tcs.TrySetException(exception);
    }
}
