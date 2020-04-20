using DuetAPI.Commands;
using DuetAPI.Machine;
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
        private static readonly AsyncLock[] _channelLocks = new AsyncLock[Inputs.Total];

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static SimpleCode()
        {
            for (int i = 0; i < Inputs.Total; i++)
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

            bool seenNewLine = true, enforcingAbsolutePositions = false;
            byte indent = 0;
            while (!reader.EndOfStream)
            {
                Code code = new Code()
                {
                    Channel = Channel,
                    Flags = enforcingAbsolutePositions ? CodeFlags.EnforceAbsolutePosition : CodeFlags.None,
                    Indent = indent,
                    SourceConnection = SourceConnection
                };

                if (DuetAPI.Commands.Code.Parse(reader, code, ref seenNewLine))
                {
                    yield return code;
                }

                enforcingAbsolutePositions = seenNewLine ? false : code.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition);
                indent = seenNewLine ? (byte)0 : code.Indent;
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
                    // M108, M112, M122, and M999 always go to an idle channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode && (code.MajorNumber == 108 || code.MajorNumber == 112 || code.MajorNumber == 122 || code.MajorNumber == 999))
                    {
                        code.Channel = await SPI.Interface.GetIdleChannel();
                        code.Flags |= CodeFlags.IsPrioritized;
                        priorityCodes.Add(code);
                    }
                    else if (IPC.Processors.Interception.IsInterceptingConnection(SourceConnection))
                    {
                        // Need to bypass the code order lock for codes being inserted...
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
                if (codes.Count > 0)
                {
                    Task<CodeResult>[] codeTasks = new Task<CodeResult>[codes.Count];
                    using (await _channelLocks[(int)Channel].LockAsync(Program.CancellationToken))
                    {
                        for (int i = 0; i < codes.Count; i++)
                        {
                            codeTasks[i] = codes[i].Execute();
                        }
                    }

                    foreach (Task<CodeResult> codeTask in codeTasks)
                    {
                        CodeResult codeResult = await codeTask;
                        if (codeResult != null)
                        {
                            result.AddRange(codeResult);
                        }
                    }
                }
            }
            catch (CodeParserException cpe)
            {
                result.Add(MessageType.Error, cpe.Message);
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