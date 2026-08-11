using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Ports;

namespace DuetControlServer.Spindles;

/// <summary>
/// The spindles a machine has, and what they are asked to turn at
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>Spindle</c>. A spindle is not a device on the CAN bus - CANlib has
/// no spindle message at all - so it is built out of three general-purpose outputs, exactly as
/// RepRapFirmware builds one out of three <c>IoPort</c>s: a PWM output that sets the speed, an on/off
/// output that starts it, and a direction output that says which way.
/// </para>
/// <para>
/// The RPM the operator asks for is mapped onto the PWM range the spindle was configured with, which
/// is what M950 R's L and F parameters describe. A spindle that reaches 24000 RPM at full PWM is
/// asked for half PWM to turn at 12000
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="gpioManager">The outputs a spindle is driven through</param>
public sealed class SpindleManager(Model.ObjectModel model, GpioManager gpioManager)
{
    /// <summary>
    /// Highest spindle number a machine may have
    /// </summary>
    /// <remarks>CANlib's <c>MaxSpindles</c>, which is the only thing it has to say about spindles</remarks>
    public const int MaxSpindles = 4;

    /// <summary>
    /// The three outputs one spindle is driven through
    /// </summary>
    /// <param name="Pwm">Output that sets the speed</param>
    /// <param name="OnOff">Output that starts and stops it, or -1 if it has none</param>
    /// <param name="Direction">Output that reverses it, or -1 if it cannot reverse</param>
    private readonly record struct SpindlePorts(int Pwm, int OnOff, int Direction);

    /// <summary>
    /// The outputs a spindle is driven through
    /// </summary>
    /// <param name="spindleNumber">The spindle</param>
    /// <returns>Its outputs, or null if it has none</returns>
    /// <remarks>
    /// Derived from the spindle's number rather than stored: the outputs are created for the spindle
    /// and numbered from the top of the range, so which ones it owns follows from which spindle it
    /// is. <c>spindles[].port</c> is what says the spindle exists and what M500 writes back out
    /// </remarks>
    private static SpindlePorts? PortsFor(Spindle spindle, int spindleNumber)
    {
        if (spindle.Port is not string ports)
        {
            return null;
        }

        int count = ports.Split('+', StringSplitOptions.RemoveEmptyEntries).Length;
        return new SpindlePorts(
            PortNumberFor(spindleNumber, 0),
            count > 1 ? PortNumberFor(spindleNumber, 1) : -1,
            count > 2 ? PortNumberFor(spindleNumber, 2) : -1);
    }

    /// <summary>
    /// The general-purpose output one of a spindle's three ports occupies
    /// </summary>
    /// <remarks>
    /// Numbered down from the top of the range, so that creating a spindle does not consume output
    /// numbers M42 might be using. The same arithmetic has to be used when the ports are created and
    /// when they are driven, which is why it is here rather than in the handler
    /// </remarks>
    public static int PortNumberFor(int spindleNumber, int index)
        => Ports.GpioManager.MaxGpOutPorts - 1 - ((spindleNumber * 3) + index);

    /// <summary>
    /// Find a spindle by number
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Spindle? Find(int spindleNumber)
        => spindleNumber >= 0 && spindleNumber < model.Spindles.Count ? model.Spindles[spindleNumber] : null;

    /// <summary>
    /// Create a spindle from the outputs that drive it
    /// </summary>
    /// <param name="spindleNumber">The spindle</param>
    /// <param name="pwmPort">Output that sets the speed</param>
    /// <param name="onOffPort">Output that starts it, or -1</param>
    /// <param name="directionPort">Output that reverses it, or -1</param>
    /// <returns>The spindle</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public Spindle Create(int spindleNumber, int pwmPort, int onOffPort, int directionPort)
    {
        while (model.Spindles.Count <= spindleNumber)
        {
            model.Spindles.Add(null);
        }

        Spindle spindle = new()
        {
            State = SpindleState.Stopped,
            Current = 0,
            Active = 0,
            CanReverse = directionPort >= 0
        };
        model.Spindles[spindleNumber] = spindle;
        return spindle;
    }

    /// <summary>
    /// Start a spindle, or change the speed of one that is running
    /// </summary>
    /// <param name="spindleNumber">The spindle</param>
    /// <param name="rpm">Requested speed</param>
    /// <param name="reverse">Whether to turn counter-clockwise</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if it could not be started, else null</returns>
    /// <remarks>
    /// The direction is set before the speed, so that a spindle which is already turning never has
    /// its direction reversed while under power. RepRapFirmware orders it the same way
    /// </remarks>
    public async ValueTask<string?> SetSpeedAsync(int spindleNumber, int rpm, bool reverse,
                                                   CancellationToken cancellationToken)
    {
        SpindlePorts ports;
        float pwm;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(spindleNumber) is not Spindle spindle)
            {
                return $"Spindle {spindleNumber} is not configured";
            }
            if (PortsFor(spindle, spindleNumber) is not SpindlePorts found)
            {
                return $"Spindle {spindleNumber} has no ports";
            }
            ports = found;
            if (reverse && spindle.CanReverse != true)
            {
                return $"Spindle {spindleNumber} cannot reverse; it has no direction port";
            }

            pwm = PwmForRpm(spindle, rpm);
            spindle.Active = rpm;
            spindle.Current = rpm;
            spindle.State = rpm == 0 ? SpindleState.Stopped
                            : reverse ? SpindleState.Reverse : SpindleState.Forward;
        }

        if (ports.Direction >= 0
            && await gpioManager.WriteAsync(ports.Direction, reverse ? 1.0f : 0.0f, isServo: false,
                                            cancellationToken) is string directionError)
        {
            return directionError;
        }

        if (await gpioManager.WriteAsync(ports.Pwm, pwm, isServo: false, cancellationToken) is string pwmError)
        {
            return pwmError;
        }

        if (ports.OnOff >= 0
            && await gpioManager.WriteAsync(ports.OnOff, rpm == 0 ? 0.0f : 1.0f, isServo: false,
                                            cancellationToken) is string onOffError)
        {
            return onOffError;
        }
        return null;
    }

    /// <summary>
    /// Stop a spindle
    /// </summary>
    public ValueTask<string?> StopAsync(int spindleNumber, CancellationToken cancellationToken)
        => SetSpeedAsync(spindleNumber, 0, reverse: false, cancellationToken);

    /// <summary>
    /// Stop every spindle
    /// </summary>
    /// <remarks>What a bare M5 does, and what stopping a job has to do</remarks>
    public async ValueTask StopAllAsync(CancellationToken cancellationToken)
    {
        List<int> spindles = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int spindleNumber = 0; spindleNumber < model.Spindles.Count; spindleNumber++)
            {
                if (model.Spindles[spindleNumber] is not null)
                {
                    spindles.Add(spindleNumber);
                }
            }
        }

        foreach (int spindleNumber in spindles)
        {
            await StopAsync(spindleNumber, cancellationToken);
        }
    }

    /// <summary>
    /// The duty cycle that turns a spindle at a speed
    /// </summary>
    /// <param name="spindle">The spindle</param>
    /// <param name="rpm">Requested speed</param>
    /// <returns>Duty cycle, 0 to 1</returns>
    /// <remarks>
    /// A linear map between the configured speed range and the configured PWM range, which is what
    /// RepRapFirmware does. A spindle asked for less than its minimum turns at its minimum rather
    /// than stopping, because the minimum is the slowest it can turn rather than a threshold
    /// </remarks>
    private static float PwmForRpm(Spindle spindle, int rpm)
    {
        if (rpm <= 0)
        {
            return spindle.IdlePwm ?? 0.0f;
        }

        int min = spindle.Min ?? 0;
        int max = spindle.Max ?? 0;
        float minPwm = spindle.MinPwm ?? 0.0f;
        float maxPwm = spindle.MaxPwm ?? 1.0f;

        if (max <= min)
        {
            return maxPwm;                      // no usable range configured, so run at full
        }

        float fraction = (float)(Math.Clamp(rpm, min, max) - min) / (max - min);
        return minPwm + (fraction * (maxPwm - minPwm));
    }
}
