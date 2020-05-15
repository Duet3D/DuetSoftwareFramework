﻿namespace DuetAPI.Machine
{
    /// <summary>
    /// Details about a general-purpose input port
    /// </summary>
    public sealed class GpInputPort : ModelObject
    {
        /// <summary>
        /// Value of this port (0..1)
        /// </summary>
        public float Value
        {
            get => _value;
			set => SetPropertyValue(ref _value, value);
        }
        private float _value;
    }
}
