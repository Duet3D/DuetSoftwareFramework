using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Static class to provide functions for invoking commands
    /// </summary>
    public static class Command
    {
        /// <summary>
        /// Execute a process, wait for it to exit, and return the stdout/stderr output
        /// </summary>
        /// <param name="fileName">File to execute</param>
        /// <param name="arguments">Command-line arguments</param>
        /// <returns>Command output</returns>
        public static Task<string> Execute(string fileName, string arguments)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                Arguments = arguments
            };
            return Execute(startInfo);
        }

        /// <summary>
        /// Execute a process, wait for it to exit, and return the stdout/stderr output
        /// </summary>
        /// <param name="startInfo">Process start info</param>
        /// <returns>Command output</returns>
        public static async Task<string> Execute(ProcessStartInfo startInfo)
        {
            StringBuilder output = new();
            void OutputReceived(object sender, DataReceivedEventArgs e)
            {
                lock (output)
                {
                    output.Append(e.Data);
                }
            };
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;

            using Process process = Process.Start(startInfo);
            process.OutputDataReceived += OutputReceived;
            process.ErrorDataReceived += OutputReceived;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(Program.CancellationToken);

            process.OutputDataReceived -= OutputReceived;
            process.ErrorDataReceived -= OutputReceived;
            return output.ToString();
        }

        /// <summary>
        /// Execute a process to check if a condition is true or false
        /// </summary>
        /// <param name="fileName">File to execute</param>
        /// <param name="arguments">Command-line arguments</param>
        /// <returns>Query result</returns>
        public static async Task<bool> ExecQuery(string fileName, string arguments)
        {
            using Process process = Process.Start(fileName, arguments);
            await process.WaitForExitAsync(Program.CancellationToken);
            return process.ExitCode == 0;
        }
    }
}
