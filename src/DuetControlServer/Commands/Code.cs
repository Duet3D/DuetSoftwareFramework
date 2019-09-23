using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetControlServer.Codes;
using DuetControlServer.IPC.Processors;
using DuetControlServer.SPI;
using Newtonsoft.Json;
using Nito.AsyncEx;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
    /// </summary>
    public class Code : DuetAPI.Commands.Code
    {
        #region Code Scheduler
        /// <summary>
        /// Queue of semaphores to guarantee the ordered execution of incoming G/M/T-codes
        /// </summary>
        private static Queue<AsyncSemaphore>[] _codeTickets;

        /// <summary>
        /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
        /// </summary>
        private static CancellationTokenSource[] _cancellationTokenSources;

        /// <summary>
        /// Initialize the code scheduler
        /// </summary>
        public static void InitScheduler()
        {
            int numCodeChannels = Enum.GetValues(typeof(CodeChannel)).Length;
            _codeTickets = new Queue<AsyncSemaphore>[numCodeChannels];
            _cancellationTokenSources = new CancellationTokenSource[numCodeChannels];
            for (int i = 0; i < numCodeChannels; i++)
            {
                _codeTickets[i] = new Queue<AsyncSemaphore>();
                _cancellationTokenSources[i] = new CancellationTokenSource();
            }
        }

        /// <summary>
        /// Cancel pending codes of the given channel
        /// </summary>
        /// <param name="channel">Channel to cancel codes from</param>
        public static void CancelPending(CodeChannel channel)
        {
            lock (_cancellationTokenSources)
            {
                // Cancel and dispose the existing CTS
                CancellationTokenSource oldCTS = _cancellationTokenSources[(int)channel];
                oldCTS.Cancel();
                oldCTS.Dispose();

                // Create a new one
                _cancellationTokenSources[(int)channel] = new CancellationTokenSource();
            }
        }

        private AsyncSemaphore GetTicket()
        {
            lock (_codeTickets)
            {
                Queue<AsyncSemaphore> tickets = _codeTickets[(int)Channel];
                if (tickets.TryPeek(out _))
                {
                    // There are codes being executed. Create a new semaphore
                    AsyncSemaphore semaphore = new AsyncSemaphore(1);
                    tickets.Enqueue(semaphore);
                    return semaphore;
                }
                else
                {
                    // No codes are being executed, we don't need to wait for execution
                    tickets.Enqueue(null);
                    return null;
                }
            }
        }

        private async Task WaitForExecution(AsyncSemaphore ticket)
        {
            // Get the current cancellation token of this channel
            CancellationToken channelToken;
            lock (_cancellationTokenSources)
            {
                channelToken = _cancellationTokenSources[(int)Channel].Token;
            }

            // Wait until the last code has been processed or sent to RRF
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancelSource.Token, channelToken))
            {
                await ticket.WaitAsync(cts.Token);
            }
        }

        private void StartNextCode()
        {
            lock (_codeTickets)
            {
                if (_codeTickets[(int)Channel].TryDequeue(out AsyncSemaphore semaphore))
                {
                    semaphore?.Release();
                }
            }
        }
        #endregion

        /// <summary>
        /// Create an empty Code instance
        /// </summary>
        public Code() { }

        /// <summary>
        /// Create a new Code instance and attempt to parse the given code string
        /// </summary>
        /// <param name="code">G/M/T-Code</param>
        public Code(string code) : base(code) { }

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        [JsonIgnore]
        public bool InternallyProcessed { get; set; }

        /// <summary>
        /// Parse multiple codes from the given input string
        /// </summary>
        /// <param name="codeString">Codes to parse</param>
        /// <returns>Enumeration of parsed G/M/T-codes</returns>
        public static IList<Code> ParseMultiple(string codeString)
        {
            // NB: Even though "yield return" seems like a good idea, it is safer to parse all the codes before any code is actually started...
            List<Code> codes = new List<Code>();
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(codeString)))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    bool enforcingAbsolutePosition = false;
                    while (!reader.EndOfStream)
                    {
                        Code code = new Code();
                        Parse(reader, code, ref enforcingAbsolutePosition);
                        codes.Add(code);
                    }
                }
            }
            return codes;
        }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="TaskCanceledException">Code has been cancelled (buffer cleared)</exception>
        public override Task<CodeResult> Execute()
        {
            if (!Flags.HasFlag(CodeFlags.IsPrioritized) && !Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                AsyncSemaphore ticket = GetTicket();
                if (ticket != null)
                {
                    return Task.Run(async () =>
                    {
                        await WaitForExecution(ticket);
                        return await Process();
                    });
                }
            }
            return Process();
        }

        private async Task<CodeResult> Process()
        {
            Console.WriteLine($"[info] Processing {this}");

            // Attempt to process the code internally
            CodeResult result = InternallyProcessed ? null : await ProcessInternally();
            if (result != null)
            {
                StartNextCode();
                return await CodeExecuted(result);
            }

            // Send it to RepRapFirmware unless it is a comment
            if (Type != CodeType.Comment)
            {
                if (Flags.HasFlag(CodeFlags.Asynchronous))
                {
                    // Enqueue the code for execution by RRF and return no result
                    Task<CodeResult> codeTask = Interface.ProcessCode(this);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            CodeResult res = await codeTask;
                            if (res != null)
                            {
                                res = await CodeExecuted(res);
                                await Model.Provider.Output(res);
                            }
                        }
                        catch (AggregateException ae)
                        {
                            Console.WriteLine($"[err] {this} -> {ae.InnerException.Message}");
                        }
                    });

                    StartNextCode();
                    return null;
                }
                else
                {
                    // RepRapFirmware buffers a number of codes so a new code can be started before the last one has finished
                    StartNextCode();

                    // Wait for the code to complete
                    result = await Interface.ProcessCode(this);
                    if (result != null)
                    {
                        result = await CodeExecuted(result);
                    }
                }
            }
            else
            {
                // Return a standard result for comments
                result = await CodeExecuted(new CodeResult());
                StartNextCode();
            }

            return result;
        }

        private async Task<CodeResult> ProcessInternally()
        {
            CodeResult result = null;

            // Preprocess this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                result = await Interception.Intercept(this, InterceptionMode.Pre);
                Flags |= CodeFlags.IsPreProcessed;

                if (result != null)
                {
                    InternallyProcessed = true;
                    return result;
                }
            }

            // Attempt to process the code internally
            switch (Type)
            {
                case CodeType.GCode:
                    result = await GCodes.Process(this);
                    break;

                case CodeType.MCode:
                    result = await MCodes.Process(this);
                    break;

                case CodeType.TCode:
                    result = await TCodes.Process(this);
                    break;
            }

            if (result != null)
            {
                InternallyProcessed = true;
                return result;
            }

            // If the code could not be interpreted internally, post-process it before it is sent to RRF
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                result = await Interception.Intercept(this, InterceptionMode.Post);
                Flags |= CodeFlags.IsPostProcessed;

                if (result != null)
                {
                    InternallyProcessed = true;
                    return result;
                }
            }

            // Code has not been interpreted yet - let RRF deal with it
            return null;
        }

        private async Task<CodeResult> CodeExecuted(CodeResult result)
        {
            // Process code result
            switch (Type)
            {
                case CodeType.GCode:
                    result = await GCodes.CodeExecuted(this, result);
                    break;

                case CodeType.MCode:
                    result = await MCodes.CodeExecuted(this, result);
                    break;

                case CodeType.TCode:
                    result = await TCodes.CodeExecuted(this, result);
                    break;
            }

            // RepRapFirmware generally prefixes error messages with the code itself.
            // Do this only for error messages that originate either from a print or from a macro file
            if (Flags.HasFlag(CodeFlags.IsFromMacro) || Channel == CodeChannel.File)
            {
                foreach (Message msg in result)
                {
                    if (msg.Type == MessageType.Error)
                    {
                        msg.Content = ToShortString() + ": " + msg.Content;
                    }
                }
            }

            // Log warning+error replies if the code could be processed internally
            if (InternallyProcessed && !result.IsEmpty)
            {
                foreach (Message msg in result)
                {
                    if (msg.Type != MessageType.Success || Channel == CodeChannel.File)
                    {
                        await Utility.Logger.Log(msg);
                    }
                }
            }

            // Finished. Optionally an "Executed" interceptor could be called here, but that would only make sense if the code reply was included
            Console.WriteLine($"[info] Completed {this}");
            return result;
        }
    }
}