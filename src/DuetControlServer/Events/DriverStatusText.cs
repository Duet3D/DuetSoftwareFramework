using DuetControlServer.Link.Protocol.Shared;
using System.Text;

namespace DuetControlServer.Events;

/// <summary>
/// The driver status word in words
/// </summary>
/// <remarks>
/// A port of CANlib's <c>StandardDriverStatus::AppendText</c>, which Duet3Expansion also calls to
/// answer a status request. The masks and the meanings come from the schema rather than from here, so
/// the two renderings cannot describe the same fault differently
/// </remarks>
public static class DriverStatusText
{
    /// <summary>
    /// Which conditions to report
    /// </summary>
    public enum Severity
    {
        /// <summary>
        /// Everything: errors, warnings and information
        /// </summary>
        All = 0,

        /// <summary>
        /// Errors and warnings
        /// </summary>
        WarningsAndErrors = 1,

        /// <summary>
        /// Errors alone
        /// </summary>
        ErrorsOnly = 2
    }

    /// <summary>
    /// Describe what a driver is reporting
    /// </summary>
    /// <param name="status">Status word as the board sent it</param>
    /// <param name="severity">Which conditions to report</param>
    /// <returns>What is set, comma-separated in bit order, or <c>ok</c> when nothing is</returns>
    public static string Describe(uint status, Severity severity)
    {
        uint relevant = status & severity switch
        {
            Severity.ErrorsOnly => StandardDriverStatus.ErrorMask,
            Severity.WarningsAndErrors => StandardDriverStatus.WarningMask | StandardDriverStatus.ErrorMask,
            _ => StandardDriverStatus.WarningMask | StandardDriverStatus.ErrorMask | StandardDriverStatus.InfoMask
        };
        if (relevant == 0)
        {
            return "ok";
        }

        StringBuilder result = new();
        for (int bit = 0; bit < StandardDriverStatus.BitMeanings.Length; bit++)
        {
            if ((relevant & (1u << bit)) != 0)
            {
                if (result.Length > 0)
                {
                    result.Append(", ");
                }
                result.Append(StandardDriverStatus.BitMeanings[bit]);
            }
        }
        return result.ToString();
    }
}
