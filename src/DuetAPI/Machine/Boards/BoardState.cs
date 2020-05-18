﻿using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Enumeration of possible expansion board states
    /// </summary>
    [JsonConverter(typeof(JsonCamelCaseStringEnumConverter))]
    public enum BoardState
    {
        /// <summary>
        /// Unknown state
        /// </summary>
        Unknown,

        /// <summary>
        /// Flashing new firmware
        /// </summary>
        Flashing,

        /// <summary>
        /// Failed to flash new firmware
        /// </summary>
        FlashFailed,

        /// <summary>
        /// Board is being reset
        /// </summary>
        Resetting,

        /// <summary>
        /// Board is up and running
        /// </summary>
        Running
    }
}
