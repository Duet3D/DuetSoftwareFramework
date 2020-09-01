using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.EvaluateExpression"/> command
    /// </summary>
    public sealed class EvaluateExpression : DuetAPI.Commands.EvaluateExpression
    {
        /// <summary>
        /// Evaluate an arbitrary expression in RepRapFirmware
        /// </summary>
        /// <returns>Evaluation result</returns>
        public override Task<object> Execute() => SPI.Interface.EvaluateExpression(Channel, Expression);
    }
}
