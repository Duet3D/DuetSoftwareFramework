using DuetAPI;
using DuetControlServer.Files;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes
{
    /// <summary>
    /// Class delegating parallel G/M/T-code execution for a single code channel.
    /// Every instance holds the code pipeline elements through which incoming G/M/T-codes are sent.
    /// Note that code files and events disrupting the code flow require their own stack level to maintain the correct order of code execution.
    /// </summary>
    public class ChannelProcessor
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
        private readonly Pipelines.PipelineBase[] _pipelines = new Pipelines.PipelineBase[NumPipelineStages];

        /// <summary>
        /// Retrieve the firmware state
        /// </summary>
        internal Pipelines.PipelineStackItem FirmwareStackItem => _pipelines[(int)PipelineStage.Firmware].CurrentStackItem;

        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="channel"></param>
        public ChannelProcessor(CodeChannel channel)
        {
            Channel = channel;
            Logger = NLog.LogManager.GetLogger(Channel.ToString());

            foreach (PipelineStage stage in Enum.GetValues(typeof(PipelineStage)))
            {
                _pipelines[(int)stage] = stage switch
                {
                    PipelineStage.Start => new Pipelines.Start(this),
                    PipelineStage.Pre => new Pipelines.Pre(this),
                    PipelineStage.ProcessInternally => new Pipelines.ProcessInternally(this),
                    PipelineStage.Post => new Pipelines.Post(this),
                    PipelineStage.Firmware => new Pipelines.Firmware(this),
                    PipelineStage.Executed => new Pipelines.Executed(this),
                    _ => throw new ArgumentException($"Unsupported pipeline stage {stage}"),
                };
            }
        }

        /// <summary>
        /// Lifecycle of this pipeline
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public Task Run() => Task.WhenAll(_pipelines.Select(stage => stage.WaitForCompletionAsync()));

        /// <summary>
        /// Get diagnostics from this pipeline
        /// </summary>
        /// <param name="builder">String builder to write to</param>
        public void Diagnostics(StringBuilder builder)
        {
            foreach (Pipelines.PipelineBase pipeline in _pipelines)
            {
                if (pipeline.Stage != PipelineStage.Firmware)
                {
                    pipeline.Diagnostics(builder);
                }
            }
        }

        /// <summary>
        /// Push a new state on the stack
        /// </summary>
        /// <returns>New pipeline state of the firmware for the SPI connector</returns>
        public Pipelines.PipelineStackItem Push(CodeFile? file)
        {
            Pipelines.PipelineStackItem? newState = null;
            foreach (PipelineStage stage in StagesWithStack)
            {
                if (stage == PipelineStage.Firmware)
                {
                    newState = _pipelines[(int)stage].Push(file);
                }
                else
                {
                    _pipelines[(int)stage].Push(file);
                }
            }
            return newState!;
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        public void Pop()
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                _pipelines[(int)stage].Pop();
            }
        }

        /// <summary>
        /// Set the job file of this channel
        /// </summary>
        /// <param name="file">Job file</param>
        public void SetJobFile(CodeFile? file)
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                _pipelines[(int)stage].SetJobFile(file);
            }
        }

        /// <summary>
        /// Check if all stages starting with a certain one are idle
        /// </summary>
        /// <param name="firstStage">First stage to check</param>
        /// <param name="code">Optional code requesting the check</param>
        /// <returns>True if the pipeline is empty</returns>
        public bool IsIdle(Commands.Code? code = null)
        {
            foreach (PipelineStage stage in StagesWithStack)
            {
                if (!_pipelines[(int)stage].IsIdle(code))
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
        /// <param name="flushAll">Whether to flush all states</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync(bool flushAll)
        {
            foreach (Pipelines.PipelineBase pipeline in _pipelines)
            {
                //Logger.Debug("Flushing codes on stage {0}", pipeline.Stage);
                if (!await pipeline.FlushAsync(flushAll))
                {
                    Logger.Debug("Failed to flush codes on stage {0}", pipeline.Stage);
                    return false;
                }
                //Logger.Debug("Flushed codes on stage {0}", pipeline.Stage);
            }
            return true;
        }

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given file to finish
        /// </summary>
        /// <param name="file">Code file</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public async Task<bool> FlushAsync(CodeFile file)
        {
            foreach (Pipelines.PipelineBase pipeline in _pipelines)
            {
                //Logger.Debug("Flushing file codes on stage {0} for {1}", pipeline.Stage, code);
                if (!await pipeline.FlushAsync(file))
                {
                    Logger.Debug("Failed to flush file codes on stage {0} for {1}", pipeline.Stage, file.FileName);
                    return false;
                }
                //Logger.Debug("Flushed file codes on stage {0} for {1}", pipeline.Stage, code);
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
            foreach (Pipelines.PipelineBase pipeline in _pipelines)
            {
                if (code.Stage == PipelineStage.Executed || pipeline.Stage > code.Stage)
                {
                    //Logger.Debug("Flushing codes on stage {0} for {1}", pipeline.Stage, code);
                    if (!await pipeline.FlushAsync(code, evaluateExpressions, evaluateAll))
                    {
                        Logger.Debug("Failed to flush codes on stage {0} for {1}", pipeline.Stage, code);
                        return false;
                    }
                    //Logger.Debug("Flushed codes on stage {0} for {1}", pipeline.Stage, code);
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
            _pipelines[(int)stage].WriteCode(code);
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
            await _pipelines[(int)stage].WriteCodeAsync(code);
            //Logger.Debug("Sent code {0} to stage {1}", code, stage);
        }
    }
}
