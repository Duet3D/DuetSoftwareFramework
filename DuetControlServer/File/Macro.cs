using DuetAPI;
using DuetAPI.Commands;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer
{
    public static partial class File
    {
        public static async Task<CodeResult> RunMacro(string fileName, Code sourceCode)
        {
            FileStream fileStream = new FileStream(fileName, FileMode.Open);
            StreamReader reader = new StreamReader(fileStream);

            // TODO push stack

            CodeResult macroResult = new CodeResult(sourceCode);

            do
            {
                string line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                try
                {
                    Code code = new Code(line, sourceCode.Source);
                    CodeResult result = await code.Execute();
                    macroResult.AddRange(result);
                }
                catch
                {
                    reader.Close();
                    fileStream.Close();
                    // TODO pop stack
                    throw;
                }
            }
            while (true);

            reader.Close();
            fileStream.Close();

            // TODO pop stack

            return macroResult;
        }
    }
}
