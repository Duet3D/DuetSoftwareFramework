using DuetAPI;
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
        public Connection? Connection { get; set; }

        /// <summary>
        /// Parse codes from the given input string asynchronously
        /// </summary>
        /// <returns>Parsed G/M/T-codes</returns>
        public async IAsyncEnumerable<Code> ParseAsync()
        {
            await using MemoryStream stream = new(Encoding.UTF8.GetBytes(Code));
            CodeParserBuffer buffer = new((int)stream.Length, Code.Contains('\n'));

            while (buffer.GetPosition(stream) < stream.Length)
            {
                Code code = new()
                {
                    Channel = Channel,
                    Connection = Connection,
                    SourceConnection = Connection?.Id ?? 0
                };

                if (await DuetAPI.Commands.Code.ParseAsync(stream, code, buffer))
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
                if (!Settings.NoSpi && Model.Provider.Get.Inputs[Channel] is null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Parse the input string
            Message result = new();
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

                    // M108, M112, M122, M292, and M999 (B0) always go to an idle channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode &&
                        (code.MajorNumber is 108 or 112 or 122 or 292 || (code.MajorNumber == 999 && code.GetInt('B', 0) == 0)))
                    {
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
            catch (CodeParserException cpe)
            {
                result.Append(MessageType.Error, cpe.Message);
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    // Repetier or other host servers expect an "ok" after error messages
                    if (Model.Provider.Get.Inputs[Channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                    {
                        result.AppendLine("ok");
                    }
                }
                return result.ToString();
            }

            try
            {
                // Execute priority codes first
                foreach (Code priorityCode in priorityCodes)
                {
                    Message? codeResult = await priorityCode.Execute();
                    try
                    {
                        if (codeResult is not null && !string.IsNullOrEmpty(codeResult.Content))
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
                    Task<Message?>[] codeTasks = new Task<Message?>[codes.Count];
                    using (await _channelLocks[(int)Channel].LockAsync(Program.CancellationToken))
                    {
                        for (int i = 0; i < codes.Count; i++)
                        {
                            codeTasks[i] = codes[i].Execute();
                        }
                    }

                    foreach (Task<Message?> codeTask in codeTasks)
                    {
                        try
                        {
                            Message? codeResult = await codeTask;
                            if (codeResult is not null)
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
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    // Repetier or other host servers expect an "ok" after error messages
                    if (Model.Provider.Get.Inputs[Channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                    {
                        result.AppendLine("ok");
                    }
                }
            }
            return result.ToString();
        }
    }
}