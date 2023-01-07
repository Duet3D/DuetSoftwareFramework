using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.EvaluateExpression"/> command
    /// </summary>
    public sealed class EvaluateExpression : DuetAPI.Commands.EvaluateExpression
    {
        /// <summary>
        /// Evaluate an arbitrary expression
        /// </summary>
        /// <returns>Evaluation result</returns>
        public override async Task<object?> Execute()
        {
            // Check if the corresponding code channel has been disabled
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Inputs[Channel] is null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Attempt to evaluate the expression internally and pass it on to RRF otherwise
            return Model.Expressions.EvaluateExpression(new Code() { Channel = Channel }, Expression, false, false);
        }
    }
}
