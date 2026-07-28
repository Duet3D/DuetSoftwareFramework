using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Utility;
using DuetControlServer.Codes;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.EvaluateExpression"/> command
/// </summary>
/// <param name="codeFactory">Code factory</param>
/// <param name="model">Object model</param>
/// <param name="expressions">Expression evaluator</param>
public sealed class EvaluateExpression(CodeFactory codeFactory, Model.ObjectModel model, Codes.Meta.Expressions expressions) : DuetAPI.Commands.EvaluateExpression
{
    /// <summary>
    /// Evaluate an arbitrary expression
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Evaluation result</returns>
    public override async Task<JsonElement> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Check if the corresponding code channel has been disabled
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Inputs[Channel] is null)
            {
                throw new InvalidOperationException("Requested code channel has been disabled");
            }
        }

        // Attempt to evaluate the expression internally and pass it on to RRF otherwise
        Code dummyCode = codeFactory.Create();
        dummyCode.Channel = Channel;

        object? result = await expressions.EvaluateExpressionToValueAsync(dummyCode, Expression, false, cancellationToken);

        // An expression can evaluate to anything, so the metadata for the result has to be looked up at runtime
        return JsonSerializer.SerializeToElement(result, JsonHelper.DefaultJsonOptions.GetTypeInfo(result?.GetType() ?? typeof(object)));
    }
}
