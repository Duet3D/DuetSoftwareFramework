using System;
using System.Collections.Generic;
using System.Text;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Representation of a simple filament monitor
    /// </summary>
    public class SimpleFilamentMonitor : FilamentMonitor
    {
        /// <summary>
        /// Indicates if a filament is present
        /// </summary>
        public bool FilamentPresent
        {
            get => _filamentPresent;
            set => SetPropertyValue(ref _filamentPresent, value);
        }
        private bool _filamentPresent;
    }
}
