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
    public sealed class SimpleCode : DuetAPI.Commands.SimpleCode, IConnectionCommand
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
            await using MemoryStream stream = new(Encoding.UTF8.GetBytes(Code));
            using StreamReader reader = new(stream);
            CodeParserBuffer buffer = new((int)stream.Length, Code.Contains('\n'));

            while (buffer.GetPosition(reader) < stream.Length)
            {
                Code code = new()
                {
                    Channel = Channel,
                    Connection = Connection,
                    SourceConnection = Connection?.Id ?? 0
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
            // Check if the corresponding code channel has been disabled
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (!Settings.NoSpi && Model.Provider.Get.Inputs[Channel] == null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Parse the input string
            List<Code> codes = new(), priorityCodes = new();
            try
            {
                await foreach (Code code in ParseAsync())
                {
                    // Check for async execution
                    if (ExecuteAsynchronously)
                    {
                        code.Flags |= CodeFlags.Asynchronous;
                    }

                    // M108, M112, M122, and M999 (B0) always go to an idle channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode &&
                        (code.MajorNumber == 108 || code.MajorNumber == 112 || code.MajorNumber == 122 || (code.MajorNumber == 999 && code.Parameter('B', 0) == 0)))
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
                return (new Message(MessageType.Error, e.Message)).ToString();
            }

            Message result = new();
            try
            {
                // Execute priority codes first
                foreach (Code priorityCode in priorityCodes)
                {
                    Message codeResult = await priorityCode.Execute();
                    try
                    {
                        if (codeResult != null && !string.IsNullOrEmpty(codeResult.Content))
                        {
                            result.AppendLine(codeResult.ToString());
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
                    Task<Message>[] codeTasks = new Task<Message>[codes.Count];
                    using (await _channelLocks[(int)Channel].LockAsync(Program.CancellationToken))
                    {
                        for (int i = 0; i < codes.Count; i++)
                        {
                            codeTasks[i] = codes[i].Execute();
                        }
                    }

                    foreach (Task<Message> codeTask in codeTasks)
                    {
                        try
                        {
                            Message codeResult = await codeTask;
                            if (codeResult != null)
                            {
                                result.Append(codeResult);
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
                result.Append(MessageType.Error, cpe.Message);
            }
            return result.ToString();
        }
    }
}