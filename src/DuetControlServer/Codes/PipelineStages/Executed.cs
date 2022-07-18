using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Handlers;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Pipeline stage to be called when a code has been resolved or cancelled.
    /// This is the only pipeline stage that cannot maintain more than one state
    /// </summary>
    public class Executed : PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Corresponding pipeline</param>
        public Executed(Pipeline pipeline) : base(Codes.PipelineStage.Executed, pipeline)
        {
            _state = _states.Peek();
        }

        /// <summary>
        /// The only state for this 
        /// </summary>
        private readonly PipelineState _state;

        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ProcessCodeAsync(Commands.Code code)
        {
            if (code.Result != null)
            {
                // Process the code result
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

                if (!code.Flags.HasFlag(CodeFlags.IsPostProcessed))
                {
                    // RepRapFirmware generally prefixes error messages with the code itself, mimic this behavior if DSF resolved this code
                    if (code.Result.Type == MessageType.Error)
                    {
                        code.Result.Content = code.ToShortString() + ": " + code.Result.Content;
                    }

                    // Messages from RRF and replies to file print codes are logged somewhere else,
                    // so we only need to log internal code replies that are not part of file prints
                    if (code.File == null || code.Channel != CodeChannel.File)
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
                            if (code.Result == null || string.IsNullOrEmpty(code.Result.Content))
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
                    else if (code.Result == null || string.IsNullOrEmpty(code.Result.Content))
                    {
                        code.Result = new Message(MessageType.Success, "\n");
                    }
                    else
                    {
                        code.Result.AppendLine(string.Empty);
                    }
                }

                // Update the last code result
                if (code.File != null)
                {
                    code.File.LastResult = (int)code.Result.Type;
                }
            }

            // Code is complete. Send it to the Executed processor and then resolve it
            try
            {
                await IPC.Processors.CodeInterception.Intercept(code, InterceptionMode.Executed);
            }
            catch (Exception e) when ((e is OperationCanceledException) != Program.CancellationToken.IsCancellationRequested)
            {
                Pipeline.Logger.Error(e, "Executed interceptor threw an exception when finishing code {0}", code);
                code.SetException(e);
            }
            finally
            {
                if (code.Result != null)
                {
                    Pipeline.Logger.Debug("Finished code {0}", code);
                    if (code.Flags.HasFlag(CodeFlags.Asynchronous))
                    {
                        if (code.Channel == CodeChannel.File)
                        {
                            await Utility.Logger.LogOutputAsync(code.Result);
                        }
                        else
                        {
                            await Model.Provider.OutputAsync(code.Result);
                        }
                    }
                    code.SetFinished();
                }
                else
                {
                    Pipeline.Logger.Debug("Cancelled code {0}", code);
                    code.SetCancelled();
                }
            }
        }

        /// <summary>
        /// Execute a given code on this pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public override void WriteCode(Commands.Code code)
        {
            if (!_state.PendingCodes.Writer.TryWrite(code))
            {
                // Allocate an extra task only if we cannot store the executed code yet. Should never get here!
                Pipeline.Logger.Warn("Pipeline failed to store code immediately so waiting synchronously for it to be added");
                _state.PendingCodes.Writer.WriteAsync(code).AsTask().Wait();
            }
        }

        /// <summary>
        /// Execute a given code on this pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <returns>Asynchronous task</returns>
        public override ValueTask WriteCodeAsync(Commands.Code code) => _state.PendingCodes.Writer.WriteAsync(code);
    }
}
