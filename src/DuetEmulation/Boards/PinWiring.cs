using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DuetEmulation.Boards;

/// <summary>
/// What a board's GPIO model needs to know from its firmware's pin table: which timer output each
/// PWM-capable pin produces when muxed to it, which EXTINT line each pin is on, and which pins toggle
/// too often to trace
/// </summary>
public static partial class PinWiring
{
    /// <summary>The monitor commands that tell a board's GPIO model about its pins</summary>
    public static IEnumerable<string> Commands(BoardType type)
    {
        if (type.Mcu != McuFamily.Same5x)
        {
            // TODO: PWM, interrupts and output tracing for the SAME70 PIO and its PWM and TC outputs
            yield break;
        }

        foreach (PinTableEntry entry in type.PinTable.Entries)
        {
            (string gpio, int number) = type.Gpio(entry.Pin);
            if (entry.TimerOutput is string tc && TcOutputPattern().Match(tc) is { Success: true } timer)
            {
                // A TC's outputs are its peripheral function E
                yield return $"{gpio} MapPwm {number} {Function('E')} tc{timer.Groups[1].Value} {timer.Groups[2].Value}";
            }
            if (entry.PwmOutput is string tcc && TccOutputPattern().Match(tcc) is { Success: true } pwm)
            {
                yield return $"{gpio} MapPwm {number} {Function(pwm.Groups[3].Value[0])} tcc{pwm.Groups[1].Value} {pwm.Groups[2].Value}";
            }
            if (entry.ExternalInterrupt is int line)
            {
                yield return $"{gpio} MapExtInt {number} {line}";
            }
        }
        foreach (McuPin step in type.StepPins)
        {
            (string gpio, int number) = type.Gpio(step);
            yield return $"{gpio} ExcludeFromTrace {number}";
        }
    }

    // Peripheral functions are numbered from A = 0
    private static int Function(char letter) => char.ToUpperInvariant(letter) - 'A';

    [GeneratedRegex(@"^tc(\d)_(\d)$")]
    private static partial Regex TcOutputPattern();

    [GeneratedRegex(@"^tcc(\d)_(\d)([A-Z])$")]
    private static partial Regex TccOutputPattern();
}
