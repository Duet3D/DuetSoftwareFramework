using System;
using System.IO;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.FileExecution
{
    /// <summary>
    /// Implementation of a macro file
    /// </summary>
    public class MacroFile : BaseFile
    {
        private enum ConfigExtraSteps
        {
            SendHostname,
            SendDateTime,
            Done
        }
        private ConfigExtraSteps _extraStep = ConfigExtraSteps.SendHostname;

        /// <summary>
        /// Whether this file is config.g or config.g.bak
        /// </summary>
        public bool IsConfig { get; set; }

        /// <summary>
        /// Whether this file is config-override.g
        /// </summary>
        public bool IsConfigOverride { get; set; }

        /// <summary>
        /// The queued code which originally started this macro file or null
        /// </summary>
        public QueuedCode StartCode { get; }

        /// <summary>
        /// Create a new macro instance
        /// </summary>
        /// <param name="fileName">Filename of the macro</param>
        /// <param name="channel">Channel to send the codes to</param>
        /// <param name="startCode">Which code is starting this macro file</param>
        public MacroFile(string fileName, CodeChannel channel, QueuedCode startCode) : base(fileName, channel)
        {
            if (startCode == null)
            {
                string name = Path.GetFileName(fileName);
                IsConfig = (name == FilePath.ConfigFile || name == FilePath.ConfigFileFallback);
                IsConfigOverride = (name == FilePath.ConfigOverrideFile);
            }
            StartCode = startCode;

            Console.WriteLine($"[info] Executing {((startCode == null) ? "system" : "nested")} macro file '{fileName}' on channel {channel}");
        }

        /// <summary>
        /// Abort the execution of this file
        /// </summary>
        public override void Abort()
        {
            Console.WriteLine($"[info] Aborted macro file '{FileName}'");
            if (StartCode != null)
            {
                StartCode.DoingNestedMacro = false;
            }
            base.Abort();
        }

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

            return null;
        }
    }
}
