using System;

namespace DuetAPI;

/// <summary>
/// Exception to be called when a G/M/T code fails
/// </summary>
/// <param name="reason"></param>
public class GCodeException(string reason) : Exception(reason)
{
    /// <summary>
    /// Reason the code through the exception
    /// </summary>
    public string Reason { get; } = reason;
}
