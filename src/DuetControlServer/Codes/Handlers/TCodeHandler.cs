using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Tools;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Class that processes T-codes in the control server
/// </summary>
/// <remarks>
/// A T-code selects the tool that later coordinates are measured to. Nothing here knows how to change
/// a tool - the machine's own <c>tfree</c>, <c>tpre</c> and <c>tpost</c> macros do, and
/// <see cref="ToolManager"/> sequences them
/// </remarks>
/// <param name="toolManager">The tools the machine has, and which one is selected</param>
/// <param name="model">Object model</param>
public sealed class TCodeHandler(ToolManager toolManager, Model.ObjectModel model) : ICodeHandler
{
    /// <inheritdoc />
    /// <remarks>
    /// No table: every T code is handled the same way, so there is no number to key on. A bare
    /// <c>T</c> reports the selected tool; anything else is a tool change, whose macros must not
    /// run while a queued move still uses the old tool's transform
    /// </remarks>
    public CodeClass? Classify(DuetAPI.Commands.Code code)
        => code.MajorNumber is null ? CodeClass.Immediate : CodeClass.Barrier;

    /// <summary>
    /// Process a T-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    /// <remarks>
    /// <para>
    /// The tool number is the code's major number, so <c>T1</c> selects tool 1 and <c>T-1</c> selects
    /// none. A bare <c>T</c> reports which tool is selected rather than changing anything.
    /// </para>
    /// <para>
    /// P is a bitmap of which macros to run, as in RepRapFirmware: <c>T1 P0</c> changes the selection
    /// and runs none of them, which is what restores the state after a power failure when the tool is
    /// already in the head
    /// </para>
    /// </remarks>
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.MajorNumber is null)
        {
            return await ReportSelectedToolAsync(cancellationToken);
        }

        ToolChangeParameters parameters = code.TryGetInt('P', out int flags)
            ? (ToolChangeParameters)flags
            : ToolChangeParameters.All;

        int toolNumber = code.MajorNumber.Value;
        string? error = await toolManager.SelectAsync(code.Channel, toolNumber < 0 ? ToolManager.NoTool : toolNumber,
                                                      parameters, code, cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }

    /// <summary>
    /// Say which tool is selected, as a bare T does
    /// </summary>
    private async ValueTask<Message> ReportSelectedToolAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            int current = model.State.CurrentTool;
            return new Message(MessageType.Success,
                               current >= 0 ? $"Tool {current} is selected" : "No tool is selected");
        }
    }

    /// <summary>
    /// React to an executed T-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
