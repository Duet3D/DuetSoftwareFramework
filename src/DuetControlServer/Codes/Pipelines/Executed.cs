using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Files;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Pipeline element for dealing with codes that have been resolved or cancelled.
    /// This is the only pipeline stage that cannot maintain more than one stack level
    /// </summary>
    public class Executed : PipelineBase
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processor">Channel processor</param>
        public Executed(ChannelProcessor processor) : base(PipelineStage.Executed, processor) => _stackItem = _stack.Peek();

        /// <summary>
        /// The only state for this
        /// </summary>
        private readonly PipelineStackItem _stackItem;

        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ProcessCodeAsync(Commands.Code code)
        {
            if (code.Result is not null)
            {
                // Update the file position
                await code.UpdateNextFilePositionAsync();

                // Notify code handlers
                switch (code.Type)
                {
                    case CodeType.GCode:
                        await GCodes.CodeExecuted(code);
                        break;

                    case CodeType.MCode:
                        await MCodes.CodeExecuted(code);
                        break;

                    case CodeType.TCode:
                        await TCodes.CodeExecuted(code);
                        break;
                }

                // Check if the result came from a DSF-only source
                if (!code.Flags.HasFlag(CodeFlags.IsPostProcessed))
                {
                    // RepRapFirmware generally prefixes error messages with the code itself, mimic this behavior if DSF resolved this code
                    if (code.Result.Type == MessageType.Error)
                    {
                        code.Result.Content = code.ToShortString() + ": " + code.Result.Content;
                    }

                    // Messages from RRF and replies to file print codes are logged somewhere else,
                    // so we only need to log internal code replies that are not part of file prints
                    if (code.File is null || !code.IsFromFileChannel)
                    {
                        await Utility.Logger.LogAsync(code.Result);
                    }
                }

                // Deal with firmware emulation
                if (!code.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    if (await code.EmulatingMarlin())
                    {
                        if (code.Flags.HasFlag(CodeFlags.IsLastCode))
                        {
                            if (code.Result is null || string.IsNullOrEmpty(code.Result.Content))
                            {
                                code.Result = new Message(MessageType.Success, "ok\n");
                            }
                            else if (code.Type == CodeType.MCode && code.MajorNumber == 105)
                            {
                                code.Result.Content = "ok " + code.Result.Content + "\n";
                            }
                            else
                            {
                                code.Result.AppendLine("ok\n");
                            }
                        }
                    }
                    else if (code.Result is null || string.IsNullOrEmpty(code.Result.Content))
                    {
                        code.Result = new Message(MessageType.Success, "\n");
                    }
                    else
                    {
                        code.Result.AppendLine(string.Empty);
                    }
                }
            }

            try
            {
                // Send it to the Executed processor
                await IPC.Processors.CodeInterception.Intercept(code, InterceptionMode.Executed);

                // Deal with its result if applicable
                if (code.Result is not null)
                {
                    // Output and log the result from async codes
                    if (code.Flags.HasFlag(CodeFlags.Asynchronous))
                    {
                        if (code.IsFromFileChannel)
                        {
                            await Utility.Logger.LogOutputAsync(code.Result);
                        }
                        else
                        {
                            await Model.Provider.OutputAsync(code.Result);
                        }
                    }

                    // Done
                    Processor.Logger.Debug("Finished code {0}", code);
                    code.SetFinished();
                }
                else
                {
                    // Cancelled
                    Processor.Logger.Debug("Cancelled code {0}", code);
                    code.SetCancelled();
                }
            }
            catch (Exception e)
            {
                // Failed to finish code (IPC error?)
                if ((e is OperationCanceledException) != Program.CancellationToken.IsCancellationRequested)
                {
                    Processor.Logger.Error(e, "Executed interceptor threw an exception when finishing code {0}", code);
                }
                code.SetException(e);
            }
        }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="file">Code file</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(CodeFile file) => _stackItem.FlushAsync();

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            return _stackItem.FlushAsync(code);
        }

        /// <summary>
        /// Execute a given code on this pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public override void WriteCode(Commands.Code code) => _stackItem.WriteCode(code);

        /// <summary>
        /// Execute a given code on this pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public override ValueTask WriteCodeAsync(Commands.Code code) => _stackItem.WriteCodeAsync(code);
    }
}
