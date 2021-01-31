using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetPluginRunner
{
    /// <summary>
    /// Service implementation of the plugin runner
    /// </summary>
    public static class Service
    {
        /// <summary>
        /// Lifecycle of this service
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            // TODO Connect to DCS
            // TODO Start last plugins
            // TODO Wait for incoming commands
            // TODO Stop last plugins and record them
        }
    }
}
