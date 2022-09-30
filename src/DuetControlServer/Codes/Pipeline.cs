using DuetAPI;
using DuetControlServer.FileExecution;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Class maintaining individual pipelines per code channel to deal with concurrent execution of G/M/T-codes
    /// </summary>
    public class Pipeline
    {
        /// <summary>
        /// Pipeline stages that support push/pop
        /// </summary>
        private readonly PipelineStage[] StagesWithStack = Enum.GetValues<PipelineStage>().Where(value => value != PipelineStage.Executed).ToArray();

        /// <summary>
        /// Number of pipelines available
        /// </summary>
        private static readonly int NumPipelineStages = Enum.GetValues(typeof(PipelineStage)).Length;

        /// <summary>
        /// Channel of this pipeline
        /// </summary>
        public readonly CodeChannel Channel;

        /// <summary>
        /// Logger instance
        /// </summary>
        public readonly NLog.Logger Logger;

        /// <summary>
        /// Pipelines for code flow
        /// </summary>
        private readonly PipelineStages.PipelineStage[] _stages = new PipelineStages.PipelineStage[NumPipelineStages];

        /// <summary>
        /// Retrieve the firmware state
        /// </summary>
        internal PipelineStages.PipelineState FirmwareState => _stages[(int)PipelineStage.Firmware].State;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="channel"></param>
        public Pipeline(CodeChannel channel)
        {
            Channel = channel;
            Logger = NLog.LogManager.GetLogger(Channel.ToString());

            foreach (PipelineStage stage in Enum.GetValues(typeof(PipelineStage)))
            {
                _stages[(int)stage] = stage switch
                {
                    PipelineStage.Start => new PipelineStages.Start(this),
                    PipelineStage.Pre => new PipelineStages.Pre(this),
                    PipelineStage.ProcessInternally => new PipelineStages.ProcessInternally(this),
                    PipelineStage.Post => new PipelineStages.Post(this),
                    PipelineStage.Firmware => new PipelineStages.Firmware(this),
                    PipelineStage.Executed => new PipelineStages.Executed(this),
                    _ => throw new ArgumentException($"Unsupported pipeline stage {stage}"),
                };
            }
        }

        /// <summary>
        /// Lifecycle of this pipeline
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public Task Run() => Task.WhenAll(_stages.Select(stage => stage.WaitForCompletionAsync()));

        /// <summary>
        /// Get diagnostics from this pipeline
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        public void Diagnostics(StringBuilder builder)
        {
            foreach (PipelineStages.PipelineStage stage in _stages)
            {
                stage.Diagnostics(builder);
            }
        }

        /// <summary>
        /// Push a new state on the stack
        /// </summary>
        /// <returns>New pipeline state of the firmware for the SPI connector</returns>
        public PipelineStages.PipelineState Push(Macro macro)
        {
            PipelineStages.PipelineState newState = null;
            foreach (PipelineStage stage in StagesWithStack)
            {
                if (stage == PipelineStage.Firmware)
                {
                    newState = _stages[(int)stage].Push(macro);
                }
                else
                {
                    _stages[(int)stage].Push(macro);
                }
            }
            return newState;
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        public void Pop()
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                _stages[(int)stage].Pop();
            }
        }

        /// <summary>
        /// Check if all stages starting with a certain one are idle
        /// </summary>
        /// <param name="firstStage">First stage to check</param>
        /// <param name="code">Optional code requesting the check</param>
        /// <returns>True if the pipeline is empty</returns>
        public bool IsIdle(Commands.Code code = null)
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                if (!_stages[(int)stage].IsIdle(code))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync()
        {
            foreach (PipelineStages.PipelineStage stage in _stages)
            {
                Logger.Debug("Flushing codes on stage {0}", stage);
                if (!await stage.FlushAsync(null))
                {
                    Logger.Debug("Failed to flush codes on stage {0}", stage);
                    return false;
                }
                Logger.Debug("Flushed codes on stage {0}", stage);
            }
            return true;
        }

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given code to finish.
        /// By default this replaces all expressions as well for convenient parsing by the code processors.
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync(Commands.Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                if (code.Stage == PipelineStage.Executed || stage > code.Stage)
                {
                    Logger.Debug("Flushing codes on stage {0} for {1}", stage, code);
                    if (!await _stages[(int)stage].FlushAsync(code, evaluateExpressions, evaluateAll))
                    {
                        Logger.Debug("Failed to flush codes on stage {0} for {1}", stage, code);
                        return false;
                    }
                    Logger.Debug("Flushed codes on stage {0} for {1}", stage, code);
                }
            }
            return true;
        }

        /// <summary>
        /// Execute a given code on this pipeline stage.
        /// This should not be used unless the corresponding code channel is unbounded
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        public void WriteCode(Commands.Code code, PipelineStage stage)
        {
            //Logger.Debug("Sending code {0} to stage {1}", code, stage);
            _stages[(int)stage].WriteCode(code);
            //Logger.Debug("Sent code {0} to stage {1}", code, stage);
        }

        /// <summary>
        /// Execute a given code on a given pipeline stage
        /// </summary>
        /// <param name="code">Code to enqueue</param>
        /// <param name="stage">Stage level to enqueue it at</param>
        public async ValueTask WriteCodeAsync(Commands.Code code, PipelineStage stage)
        {
            //Logger.Debug("Sending code {0} to stage {1}", code, stage);
            await _stages[(int)stage].WriteCodeAsync(code);
            //Logger.Debug("Sent code {0} to stage {1}", code, stage);
        }
    }
}
