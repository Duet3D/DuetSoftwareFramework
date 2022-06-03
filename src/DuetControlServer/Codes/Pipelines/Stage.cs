using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Enumeration of different stages in the code pipeline
    /// </summary>
    public enum Stage
    {
        /// <summary>
        /// Code has been read from a G-code source
        /// </summary>
        Input,

        /// <summary>
        /// Code is intercepted by third-party plugins (pre stage)
        /// </summary>
        Pre,

        /// <summary>
        /// Code is internally executed
        /// </summary>
        Execute,

        /// <summary>
        /// Code is intercepted by third-party plugins (post stage)
        /// </summary>
        Post,

        /// <summary>
        /// Code is processed by the firmware
        /// </summary>
        Firmware
    }
}
