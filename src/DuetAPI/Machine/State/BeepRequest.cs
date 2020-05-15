﻿namespace DuetAPI.Machine
{
    /// <summary>
    /// Details about a requested beep
    /// </summary>
    public sealed class BeepRequest : ModelObject
    {
        /// <summary>
        /// Duration of the requested beep (in ms)
        /// </summary>
        public int Duration
        {
            get => _duration;
			set => SetPropertyValue(ref _duration, value);
        }
        private int _duration;

        /// <summary>
        /// Frequency of the requested beep (in Hz)
        /// </summary>
        public int Frequency
        {
            get => _frequency;
			set => SetPropertyValue(ref _frequency, value);
        }
        private int _frequency;
    }
}
