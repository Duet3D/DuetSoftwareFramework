﻿using DuetControlServer.SPI;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Flush"/> command
    /// </summary>
    public class Flush : DuetAPI.Commands.Flush
    {
        /// <summary>
        /// Source connection of this command
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task<bool> Execute() => Interface.Flush(Channel);
    }
}
