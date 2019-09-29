using DuetAPI.Commands;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.SimpleCode"/> command
    /// </summary>
    public class SimpleCode : DuetAPI.Commands.SimpleCode
    {
        /// <summary>
        /// Converts simple G/M/T-codes to a regular Code instances, executes them and returns the result as text
        /// </summary>
        /// <returns>Code result as text</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override async Task<string> Execute()
        {
            CodeResult result = new CodeResult();

            try
            {
                List<Code> codes = Commands.Code.ParseMultiple(Code);
                foreach (Code code in codes)
                {
                    // M112, M122, and M999 always go to the Daemon channel so we (hopefully) get a low-latency response
                    if (code.Type == CodeType.MCode && (code.MajorNumber == 112 || code.MajorNumber == 122 || code.MajorNumber == 999))
                    {
                        code.Channel = DuetAPI.CodeChannel.Daemon;
                        code.Flags |= CodeFlags.IsPrioritized;
                    }
                    else
                    {
                        code.Channel = Channel;
                    }

                    // Execute the code and append the result
                    CodeResult codeResult = await code.Execute();
                    result.AddRange(codeResult);
                }
            }
            catch (CodeParserException e)
            {
                // Report parsing errors as an error message
                result = new CodeResult(DuetAPI.MessageType.Error, e.Message);
            }
            catch (OperationCanceledException)
            {
                // Report this code as cancelled
                result.Add(DuetAPI.MessageType.Error, "Code has been cancelled");
            }

            return result.ToString();
        }
    }
}