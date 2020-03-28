namespace DuetAPI.Commands
{
    /// <summary>
    /// Evaluate an arbitrary expression in RepRapFirmware
    /// </summary>
    /// <remarks>
    /// Do not use this call to evaluate file-based and network-related fields because the DSF and RRF models diverge in this regard
    /// </remarks>
    public class EvaluateExpression : Command<object>
    {
        /// <summary>
        /// Expression to evaluate
        /// </summary>
        public string Expression { get; set; }
    }
}
