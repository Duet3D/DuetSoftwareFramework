using DynamicExpresso;
using System;
using System.Collections.Generic;
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
        public override async Task<object> Execute()
        {
            object result;
            if (Settings.NoSpi)
            {
                // Cannot ask RRF in standalone operation, evaluate it manually
                Interpreter interpreter = new Interpreter(InterpreterOptions.DefaultCaseInsensitive).EnableAssignment(AssignmentOperators.None);
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    List<Parameter> parameters = new List<Parameter>();
                    foreach (var kv in Model.Provider.Get.JsonProperties)
                    {
                        parameters.Add(new Parameter(kv.Key, kv.Value.GetValue(Model.Provider.Get)));
                    }
                    result = interpreter.Eval(Expression, parameters.ToArray());
                }
            }
            else
            {
                // Check if the corresponding code channel has been disabled
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    if (Model.Provider.Get.Inputs[Channel] == null)
                    {
                        throw new InvalidOperationException("Requested code channel has been disabled");
                    }
                }

                // Ask RRF to evaluate the expression
                result = await SPI.Interface.EvaluateExpression(Channel, Expression);
            }
            return result;
        }
    }
}
