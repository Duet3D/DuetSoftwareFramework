using DuetAPI;
using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SimpleCode"/> command
    /// </summary>
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        /// <summary>
        /// Locks to avoid race conditions when executing multiple text-based codes via the same channel
        /// </summary>
        private static readonly AsyncLock[] _channelLocks = new AsyncLock[DuetAPI.Machine.Channels.Total];

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            for (int i = 0; i < DuetAPI.Machine.Channels.Total; i++)
            {
                _channelLocks[i] = new AsyncLock();
            }
        }

        /// <summary>
        /// Source connection of this command
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Parse codes from the given input string
        /// </summary>
        /// <returns>Parsed G/M/T-codes</returns>
        public IEnumerable<Code> Parse()
        {
            using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(Code));
            using StreamReader reader = new StreamReader(stream);
            bool enforcingAbsolutePosition = false;
            while (!reader.EndOfStream)
            {
                Code code = new Code() { Channel = Channel, SourceConnection = SourceConnection };
                DuetAPI.Commands.Code.Parse(reader, code, ref enforcingAbsolutePosition);
                yield return code;
            }
        }

        /// <summary>
        /// Converts simple G/M/T-codes to a regular Code instances, executes them and returns the result as text
        /// </summary>
        /// <returns>Code result as text</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override async Task<string> Execute()
        {
            // Parse the input string
            List<Code> codes = new List<Code>(), priorityCodes = new List<Code>();
            try
            {
                foreach (Code code in Parse())
                {
                    // M112, M122, and M999 always go to the Daemon channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode && (code.MajorNumber == 112 || code.MajorNumber == 122 || code.MajorNumber == 999))
                    {
                        code.Channel = CodeChannel.Daemon;
                        code.Flags |= CodeFlags.IsPrioritized;
                        priorityCodes.Add(code);
                    }
                    else
                    {
                        codes.Add(code);
                    }
                }
            }
            catch (CodeParserException e)
            {
                // Report parsing errors as an error message
                return (new CodeResult(MessageType.Error, e.Message)).ToString();
            }

            CodeResult result = new CodeResult();
            try
            {
                // Execute priority codes first
                foreach (Code priorityCode in priorityCodes)
                {
                    CodeResult codeResult = await priorityCode.Execute();
                    if (codeResult != null)
                    {
                        result.AddRange(codeResult);
                    }
                }

                // Execute normal codes next. Use a lock here because multiple codes may be queued for the same channel
                using (await _channelLocks[(int)Channel].LockAsync())
                {
                    foreach (Code code in codes)
                    {
                        CodeResult codeResult = await code.Execute();
                        if (codeResult != null)
                        {
                            result.AddRange(codeResult);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Report when a code is cancelled
                result.Add(MessageType.Error, "Code has been cancelled");
            }
            return result.ToString();
        }
    }
}