using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Evaluate an arbitrary expression on the given channel
    /// </summary>
    [RequiredPermissions(SbcPermissions.CommandExecution)]
    public class EvaluateExpression : Command<object?>
    {
        /// <summary>
        /// Code channel where the expression is evaluated
        /// </summary>
        public CodeChannel Channel { get; set; }

        /// <summary>
        /// Expression to evaluate
        /// </summary>
        public string Expression { get; set; } = string.Empty;
    }
}
