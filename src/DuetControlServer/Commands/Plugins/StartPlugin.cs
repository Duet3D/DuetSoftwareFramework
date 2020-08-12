using DuetAPI.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.StartPlugin"/> command
    /// </summary>
    public class StartPlugin : DuetAPI.Commands.StartPlugin
    {
        /// <summary>
        /// Start a plugin
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        await StartProcess(item);
                        break;
                    }
                }
            }
        }

        private async Task StartProcess(Plugin plugin)
        {
            // Start the plugin process
            string sbcExecutable = Path.Combine(Settings.PluginDirectory, plugin.Name, plugin.SbcExecutable);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = sbcExecutable,
                Arguments = plugin.SbcExecutableArguments,
                WorkingDirectory = Path.GetDirectoryName(sbcExecutable),
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            Process process = Process.Start(startInfo);
            DataReceivedEventHandler outputHandler = MakeOutputHandler(Plugin, MessageType.Success);
            DataReceivedEventHandler errorHandler = MakeOutputHandler(Plugin, MessageType.Error);
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;

            // Update the PID
            using (await Model.Provider.AccessReadWriteAsync())
            {
                foreach (Plugin item in Model.Provider.Get.Plugins)
                {
                    if (item.Name == Plugin)
                    {
                        item.PID = process.Id;
                    }
                }
            }

            // Wait for the plugin to terminate in the background
            _ = Task.Run(async delegate
            {
                try
                {
                    // Wait for it to be terminated
                    process.WaitForExit();
                    process.ErrorDataReceived -= errorHandler;
                    process.OutputDataReceived -= outputHandler;

                    // Update the PID again
                    using (await Model.Provider.AccessReadWriteAsync())
                    {
                        foreach (Plugin item in Model.Provider.Get.Plugins)
                        {
                            if (item.Name == Plugin)
                            {
                                item.PID = -1;
                            }
                        }
                    }

                    // Kill any leftover child processes
                    process.Kill(true);
                }
                finally
                {
                    process.Dispose();
                }
            });
        }

        private static DataReceivedEventHandler MakeOutputHandler(string pluginName, MessageType messageType)
        {
            return (object sender, DataReceivedEventArgs e) =>
            {
                Model.Provider.Output(messageType, $"[{pluginName}]: {e.Data}");
            };
        }
    }
}
