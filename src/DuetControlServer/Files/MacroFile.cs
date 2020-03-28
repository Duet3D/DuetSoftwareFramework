using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Files
{
    /// <summary>
    /// Implementation of a macro file
    /// </summary>
    public class MacroFile : BaseFile
    {
        /// <summary>
        /// Indicates if config.g is being processed
        /// </summary>
        public static bool RunningConfig { get => _runningConfig; }
        private static volatile bool _runningConfig;

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly NLog.Logger _logger;

        /// <summary>
        /// Whether this file is config.g or config.g.bak
        /// </summary>
        public bool IsConfig { get; }

        /// <summary>
        /// Whether this file is config-override.g
        /// </summary>
        public bool IsConfigOverride { get; }

        /// <summary>
        /// The queued code which originally started this macro file or null
        /// </summary>
        public QueuedCode StartCode { get; }

        /// <summary>
        /// Pending codes being started by a nested macro (and multiple codes may be started by an interceptor).
        /// This is required because it may take a moment until they are internally processed
        /// </summary>
        public Queue<QueuedCode> PendingCodes { get; } = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public Queue<TaskCompletionSource<bool>> PendingFlushRequests { get; } = new Queue<TaskCompletionSource<bool>>();

        /// <summary>
        /// Create a new macro instance
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="channel">Channel to send the codes to</param>
        /// <param name="startCode">Which code is starting this macro file</param>
        public MacroFile(string fileName, CodeChannel channel, QueuedCode startCode = null) : base(fileName, channel)
        {
            StartCode = startCode;

            string name = Path.GetFileName(fileName);
            if (startCode == null)
            {
                if (name == FilePath.ConfigFile || name == FilePath.ConfigFileFallback)
                {
                    IsConfig = true;
                    _runningConfig = true;
                }
                IsConfigOverride = (name == FilePath.ConfigOverrideFile);
            }

            _logger = NLog.LogManager.GetLogger(Path.GetFileName(fileName));
            _logger.Info("Executing {0} macro file {1} on channel {2}", (startCode == null) ? "system" : "nested", name, channel);
            if (startCode != null)
            {
                _logger.Debug("==> Starting code: {0}", startCode);
            }
        }

        /// <summary>
        /// Called when this instance is being disposed
        /// </summary>
        /// <param name="disposing">True if managed resources are being disposed</param>
        protected override void Dispose(bool disposing)
        {
            // Synchronize the machine model one last time while config.g or config-override.g is being run.
            // This makes sure the filaments can be properly assigned
            if (IsConfig || IsConfigOverride)
            {
                _ = Task.Run(async () =>
                {
                    await Model.Updater.WaitForFullUpdate(Program.CancellationToken);
                    _runningConfig = false;
                });
            }

            // Dispose this instance
            base.Dispose(disposing);
        }

        /// <summary>
        /// Abort the execution of this file
        /// </summary>
        public override void Abort()
        {
            while (PendingCodes.TryDequeue(out QueuedCode item))
            {
                if (!item.IsFinished)
                {
                    item.SetCancelled();
                }
            }

            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> tcs))
            {
                tcs.TrySetResult(false);
            }

            if (StartCode != null)
            {
                StartCode.DoingNestedMacro = false;
            }

            base.Abort();
            _logger.Info("Aborted macro file");
        }

        /// <summary>
        /// Extra steps to perform before config.g is processed
        /// </summary>
        private enum ConfigExtraSteps
        {
            SendHostname,
            SendDateTime,
            Done
        }

        /// <summary>
        /// Current extra step being performed (provided config.g is being executed)
        /// </summary>
        private ConfigExtraSteps _extraStep = ConfigExtraSteps.SendHostname;

        /// <summary>
        /// Read another code from the file being executed asynchronously
        /// </summary>
        /// <returns>Next available code or null if the file has ended</returns>
        public override Code ReadCode()
        {
            Code result;

            // When executing config.g, perform some extra steps...
            if (IsConfig)
            {
                switch (_extraStep)
                {
                    case ConfigExtraSteps.SendHostname:
                        _logger.Debug("Sending hostname");
                        result = new Code
                        {
                            Channel = Channel,
                            InternallyProcessed = true,          // don't check our own hostname
                            Type = CodeType.MCode,
                            MajorNumber = 550
                        };
                        result.Parameters.Add(new CodeParameter('P', Environment.MachineName));
                        _extraStep = ConfigExtraSteps.SendDateTime;
                        break;

                    case ConfigExtraSteps.SendDateTime:
                        _logger.Debug("Sending datetime");
                        result = new Code
                        {
                            Channel = Channel,
                            InternallyProcessed = true,          // don't update our own datetime
                            Type = CodeType.MCode,
                            MajorNumber = 905
                        };
                        result.Parameters.Add(new CodeParameter('P', DateTime.Now.ToString("yyyy-MM-dd")));
                        result.Parameters.Add(new CodeParameter('S', DateTime.Now.ToString("HH:mm:ss")));
                        _extraStep = ConfigExtraSteps.Done;
                        break;

                    default:
                        result = base.ReadCode();
                        break;
                }
            }
            else
            {
                result = base.ReadCode();
            }

            // Update code information
            if (result != null)
            {
                result.FilePosition = null;
                result.Flags |= CodeFlags.IsFromMacro;
                if (IsConfig) { result.Flags |= CodeFlags.IsFromConfig; }
                if (IsConfigOverride) { result.Flags |= CodeFlags.IsFromConfigOverride; }
                if (StartCode != null) { result.Flags |= CodeFlags.IsNestedMacro; }
                result.SourceConnection = (StartCode != null) ? StartCode.Code.SourceConnection : 0;
                return result;
            }

            // File has finished
            return null;
        }
    }
}
