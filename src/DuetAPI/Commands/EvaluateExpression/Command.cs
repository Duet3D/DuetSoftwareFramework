namespace DuetAPI.Commands
{
    /// <summary>
    /// Evaluate an arbitrary expression on the given channel in RepRapFirmware
    /// </summary>
    /// <remarks>
    /// Do not use this call to evaluate file-based and network-related fields because the DSF and RRF models diverge in this regard
    /// </remarks>
    public class EvaluateExpression : Command<object>
    {
        /// <summary>
        /// Code channel where the expression is evaluated
        /// </summary>
        public CodeChannel Channel { get; set; }

        /// <summary>
        /// Expression to evaluate
        /// </summary>
        public string Expression { get; set; }
    }
}
