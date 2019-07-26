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
        /// <returns>G-code result as text</returns>
        public override async Task<string> Execute()
        {
            IList<Code> codes;
            Queue<Task<CodeResult>> codeTasks = new Queue<Task<CodeResult>>();

            CodeResult result = new CodeResult();
            try
            {
                codes = Commands.Code.ParseMultiple(Code);
                foreach (Code code in codes)
                {
                    // M122 always goes to the Daemon channel so we (hopefully) get a low-latency response
                    code.Channel = (code.Type == CodeType.MCode && code.MajorNumber == 122) ? DuetAPI.CodeChannel.Daemon : Channel;
                    code.SourceConnection = SourceConnection;

                    codeTasks.Enqueue(code.Enqueue());
                }
            }
            catch (CodeParserException e)
            {
                return $"Error: {e.Message}]\n";
            }

            while (codeTasks.TryDequeue(out Task<CodeResult> task))
            {
                Code code = codes[0];
                codes.RemoveAt(0);

                try
                {
                    CodeResult codeResult = await task;
                    if (codeResult != null && !codeResult.IsEmpty)
                    {
                        result.AddRange(codeResult);
                    }
                }
                catch (AggregateException ae)
                {
                    // FIXME: Should this terminate the code(s) being executed?
                    Console.WriteLine($"[err] {code} -> {ae.InnerException.Message}");
                }
            }

            return result.ToString();
        }
    }
}