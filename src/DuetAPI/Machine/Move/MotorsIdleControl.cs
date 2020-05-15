﻿namespace DuetAPI.Machine
{
    /// <summary>
    /// Idle factor parameters for automatic motor current reduction
    /// </summary>
    public sealed class MotorsIdleControl : ModelObject
    {
        /// <summary>
        /// Idle timeout after which the stepper motor currents are reduced (in s)
        /// </summary>
        public float Timeout
        {
            get => _timeout;
			set => SetPropertyValue(ref _timeout, value);
        }
        private float _timeout = 30F;
        
        /// <summary>
        /// Motor current reduction factor (0..1)
        /// </summary>
        public float Factor
        {
            get => _factor;
			set => SetPropertyValue(ref _factor, value);
        }
        private float _factor = 0.3F;
    }
}