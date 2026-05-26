using DuetAPI.Utility;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Communication method used to talk to the firmware
/// </summary>
[JsonConverter(typeof(JsonCamelCaseStringEnumConverter<CommunicationMethod>))]
public enum CommunicationMethod
{
    /// <summary>
    /// SPI link adapter
    /// </summary>
    SPI,

    /// <summary>
    /// USB link adapter
    /// </summary>
    USB
}
