using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        /// List of macro files being executed
        /// </summary>
        private static readonly List<MacroFile> _macroFiles = new List<MacroFile>();

        /// <summary>
        /// Indicates if a file macro is being done
        /// </summary>
        public static bool DoingMacroFile
        {
            get
            {
                lock (_macroFiles)
                {
                    return _macroFiles.Count != 0;
                }
            }
        }

        /// <summary>
        /// Abort files on the given channel (probably because the firmware requested this)
        /// </summary>
        /// <param name="channel">Channel on which macros are supposed to be cancelled</param>
        /// <returns>If an abortion could be requested</returns>
        public static bool AbortAllFiles(CodeChannel channel)
        {
            bool filesAborted = false;
            lock (_macroFiles)
            {
                foreach (MacroFile file in _macroFiles.ToList())
                {
                    if (file.Channel == channel)
                    {
                        file.Abort();
                        _macroFiles.Remove(file);
                        filesAborted = true;
                    }
                }
            }
            return filesAborted;
        }

        /// <summary>
        /// Abort the last file on the given channel
        /// </summary>
        /// <param name="channel">Channel of the running macro file</param>
        /// <returns>If an abortion could be requested</returns>
        public static bool AbortLastFile(CodeChannel channel)
        {
            lock (_macroFiles)
            {
                foreach (MacroFile file in _macroFiles)
                {
                    if (file.Channel == channel)
                    {
                        file.Abort();
                        _macroFiles.Remove(file);
                        return true;
                    }
                }
            }
            return false;
        }

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
            lock (_macroFiles)
            {
                _macroFiles.Add(this);
            }

            Console.WriteLine($"[info] Executing {((startCode == null) ? "system" : "nested")} macro file '{fileName}' on channel {channel}");
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        public static void Diagnostics(StringBuilder builder)
        {
            lock (_macroFiles)
            {
                foreach (MacroFile file in _macroFiles)
                {
                    builder.AppendLine($"Executing {((file.StartCode == null) ? "system" : "nested")} macro file '{file.FileName}' on channel {file.Channel}");
                }
            }
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

            // Clean up again when the macro file has been finished
            if (result == null)
            {
                lock (_macroFiles)
                {
                    _macroFiles.Remove(this);
                }
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
