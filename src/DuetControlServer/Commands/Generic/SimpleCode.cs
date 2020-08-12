using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.IPC;
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
    public class SimpleCode : DuetAPI.Commands.SimpleCode, IConnectionCommand
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
        public Connection Connection { get; set; }

        /// <summary>
        /// Parse codes from the given input string asynchronously
        /// </summary>
        /// <returns>Parsed G/M/T-codes</returns>
        public async IAsyncEnumerable<Code> ParseAsync()
        {
            using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(Code));
            using StreamReader reader = new StreamReader(stream);
            CodeParserBuffer buffer = new CodeParserBuffer((int)stream.Length, Code.Contains('\n'));

            while (buffer.GetPosition(reader) < stream.Length)
            {
                Code code = new Code()
                {
                    Channel = Channel,
                    Connection = Connection
                };

                if (await DuetAPI.Commands.Code.ParseAsync(reader, code, buffer))
                {
                    yield return code;
                }
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
                await foreach (Code code in ParseAsync())
                {
                    // M108, M112, M122, and M999 always go to an idle channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode && (code.MajorNumber == 108 || code.MajorNumber == 112 || code.MajorNumber == 122 || code.MajorNumber == 999))
                    {
                        code.Channel = await SPI.Interface.GetIdleChannel();
                        code.Flags |= CodeFlags.IsPrioritized;
                        priorityCodes.Add(code);
                    }
                    else if (IPC.Processors.CodeInterception.IsInterceptingConnection(Connection))
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
                    try
                    {
                        if (codeResult != null)
                        {
                            result.AddRange(codeResult);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // not logged
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
                        try
                        {
                            CodeResult codeResult = await codeTask;
                            if (codeResult != null)
                            {
                                result.AddRange(codeResult);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // not logged
                        }
                    }
                }
            }
            catch (CodeParserException cpe)
            {
                result.Add(MessageType.Error, cpe.Message);
            }
            return result.ToString();
        }
    }
}