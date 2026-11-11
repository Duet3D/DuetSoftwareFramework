using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Fans.FanSetup;

namespace SystemTests.Scenarios.Codes.Fans;

/// <summary>
/// M106, fan speed and configuration, thermostatic fans and the restore points that save a speed
/// </summary>
[TestFixture]
[GCode("M106")]
public class M106Tests : BenchFixture
{
    /// <summary>
    /// M106 S reads a value of at most 1 as a fraction and anything above as a PWM byte out of 255,
    /// so both forms land on the same 0..1 requestedValue
    /// </summary>
    /// <remarks>
    /// GCodeBuffer::GetPwmValue (GCodeBuffer.cpp) divides values above 1 by 255 and constrains to
    /// 0..1; case 106 in GCodes2.cpp writes the result through FansManager::SetFanValue into
    /// Fan::val, which the OM reports as requestedValue (Fan.cpp)
    /// </remarks>
    [Test]
    public async Task M106ScalesSpeedFractionAndByteAlike()
    {
        await using Bench bench = await Bench.StartAsync(TwoFanConfig);

        Fan? fan0 = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Fan? fan1 = await bench.Host.ReadModelAsync(model => model.Fans[1]);

        Assert.Multiple(() =>
        {
            Assert.NotNull(fan0);
            Assert.NotNull(fan1);
        });

        await bench.Host.ExecuteCodeAsync("M106 P0 S0.5");
        Assert.That(fan0!.RequestedValue, Is.EqualTo(0.5).Within(1e-3),
                    "M106 S0.5 sets fans[0].requestedValue as a fraction (GCodeBuffer::GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M106 P0 S128");
        Assert.That(fan0!.RequestedValue, Is.EqualTo(128.0 / 255.0).Within(1e-3),
                    "M106 S128 sets fans[0].requestedValue to 128/255 (GCodeBuffer::GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M106 P0 S255");
        Assert.That(fan0!.RequestedValue, Is.EqualTo(1.0).Within(1e-3),
                    "M106 S255 sets fans[0].requestedValue to 1 (GCodeBuffer::GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M106 P1 S0.75");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(fan1!.RequestedValue, Is.EqualTo(0.75).Within(1e-3),
                        "M106 P1 S0.75 sets fans[1].requestedValue (GCodes2.cpp case 106)");
            Assert.That(fan0!.RequestedValue, Is.EqualTo(1.0).Within(1e-3),
                        "M106 P1 leaves fans[0].requestedValue alone (GCodes2.cpp case 106)");
        });

        // Each speed reached the board as a SetFanSpeed carrying the scaled value, in the order the
        // codes ran: RemoteFan::Refresh sends the fan number and the PWM and nothing else, so the
        // scaling has to have happened on this side
        Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>()
                         .Select(sent => (sent.Board, sent.Message.FanNumber, Pwm: MathF.Round(sent.Message.Pwm, 3))),
                    Is.EqualTo(new (byte, ushort, float)[]
                    {
                        (Machines.DriverBoard, 0, 0.5f),
                        (Machines.DriverBoard, 0, MathF.Round(128.0f / 255.0f, 3)),
                        (Machines.DriverBoard, 0, 1.0f),
                        (Machines.DriverBoard, 1, 0.75f)
                    }),
                    "one SetFanSpeed per M106 S, each naming its fan and the 0..1 value it scaled to "
                    + "(RemoteFan::Refresh)");
    }

    /// <summary>
    /// M106 H and T configure thermostatic mode; B, L and X set blip, minimum and maximum; S is
    /// acted on alongside the other parameters; H-1 disables thermostatic mode, which nulls the
    /// trigger temperatures
    /// </summary>
    /// <remarks>
    /// Fan::Configure (Fan.cpp): T fills triggerTemperatures, padding a single value to both; H
    /// rebuilds sensorsMonitored; S is acted on only alongside other parameters and after H; B is
    /// seconds to milliseconds and back; L is clamped to max, X to min. The OM table reports
    /// lowTemperature and highTemperature only while sensorsMonitored is non-empty
    /// (OBJECT_MODEL_FUNC_IF), so both read null after H-1
    /// </remarks>
    [Test]
    public async Task M106ThermostaticModeParameters()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config + $"\nM308 S1 P\"{Expansion.Temp(Machines.DriverBoard, 0)}\" Y\"thermistor\"");

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);

        await bench.Host.ExecuteCodeAsync("M106 P0 H1 T45");
        FanThermostaticControl thermostatic = fan!.Thermostatic;
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(thermostatic!.Sensors.Count, Is.EqualTo(1),
                        "M106 H1 makes fans[0].thermostatic.sensors hold one sensor (Fan.cpp sensorsMonitored)");
            Assert.That(thermostatic!.Sensors[0], Is.EqualTo(1),
                        "M106 H1 monitors sensor 1 (Fan.cpp Fan::Configure)");
            Assert.That(thermostatic!.LowTemperature, Is.EqualTo(45).Within(1e-3),
                        "a single M106 T value pads to fans[0].thermostatic.lowTemperature (Fan.cpp GetFloatArray padding)");
            Assert.That(thermostatic!.HighTemperature, Is.EqualTo(45).Within(1e-3),
                        "a single M106 T value pads to fans[0].thermostatic.highTemperature (Fan.cpp GetFloatArray padding)");
        });

        await bench.Host.ExecuteCodeAsync("M106 P0 H1 T40:70 B0.5 L0.25 X0.8 S0.5");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(fan!.Thermostatic.LowTemperature, Is.EqualTo(40).Within(1e-3),
                        "M106 T40:70 sets fans[0].thermostatic.lowTemperature (Fan.cpp triggerTemperatures[0])");
            Assert.That(fan!.Thermostatic.HighTemperature, Is.EqualTo(70).Within(1e-3),
                        "M106 T40:70 sets fans[0].thermostatic.highTemperature (Fan.cpp triggerTemperatures[1])");
            Assert.That(fan!.Blip, Is.EqualTo(0.5).Within(1e-3),
                        "M106 B0.5 sets fans[0].blip in seconds (Fan.cpp blipTime)");
            Assert.That(fan!.Min, Is.EqualTo(0.25).Within(1e-3),
                        "M106 L0.25 sets fans[0].min (Fan.cpp minVal)");
            Assert.That(fan!.Max, Is.EqualTo(0.8).Within(1e-3),
                        "M106 X0.8 sets fans[0].max (Fan.cpp maxVal)");
            Assert.That(fan!.RequestedValue, Is.EqualTo(0.5).Within(1e-3),
                        "M106 S0.5 alongside H is acted on after it: fans[0].requestedValue 0.5 (Fan.cpp, S processed with other parameters)");
        });

        (byte board, CanMessageFanParameters configured) = bench.CanMaster.LastCanMessage<CanMessageFanParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the parameters go to the board that reads the sensors");
            Assert.That(configured.FanNumber, Is.EqualTo(0));
            Assert.That(configured.SensorsMonitored, Is.EqualTo(1UL << 1),
                        "H1 reaches the board as a bitmap with sensor 1's bit set: thermostatic control is the "
                        + "board's rule to apply, because it is the board that reads the sensor");
            Assert.That(configured.TriggerTemperatures[0], Is.EqualTo(40).Within(1e-3), "T's low temperature");
            Assert.That(configured.TriggerTemperatures[1], Is.EqualTo(70).Within(1e-3), "and its high one");
            Assert.That(configured.Val, Is.EqualTo(0.5).Within(1e-3), "the speed S asked for");
            Assert.That(configured.MinVal, Is.EqualTo(0.25).Within(1e-3), "L");
            Assert.That(configured.MaxVal, Is.EqualTo(0.8).Within(1e-3), "X");
            Assert.That(configured.BlipTime, Is.EqualTo(500), "and B0.5 as 500 ms");
        });

        Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Is.Empty,
                    "no SetFanSpeed went with any of it: an S alongside other parameters rides in the "
                    + "parameters message, which is the only thing Fan::Configure sends (Fan.cpp)");

        await bench.Host.ExecuteCodeAsync("M106 P0 H-1");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(fan!.Thermostatic.Sensors.Count, Is.Zero,
                        "M106 H-1 clears fans[0].thermostatic.sensors (Fan.cpp sensorsMonitored.Clear)");
            Assert.That(fan!.Thermostatic.LowTemperature, Is.Null,
                        "with no monitored sensors fans[0].thermostatic.lowTemperature reads null (Fan.cpp OBJECT_MODEL_FUNC_IF)");
            Assert.That(fan!.Thermostatic.HighTemperature, Is.Null,
                        "with no monitored sensors fans[0].thermostatic.highTemperature reads null (Fan.cpp OBJECT_MODEL_FUNC_IF)");
        });

        (byte clearedBoard, CanMessageFanParameters cleared) = bench.CanMaster.LastCanMessage<CanMessageFanParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(clearedBoard, Is.EqualTo(Machines.DriverBoard));
            Assert.That(cleared.FanNumber, Is.EqualTo(0));
            Assert.That(cleared.SensorsMonitored, Is.Zero,
                        "M106 H-1 leaves the board watching nothing, which is what turns thermostatic mode off "
                        + "there rather than here (Fan.cpp sensorsMonitored.Clear)");
            Assert.That(cleared.Val, Is.EqualTo(0.5).Within(1e-3),
                        "and the rest of the fan's state goes with it: UpdateFanConfiguration sends the whole "
                        + "struct every time, not the parameters that changed (RemoteFan.cpp)");
            Assert.That(cleared.MinVal, Is.EqualTo(0.25).Within(1e-3));
            Assert.That(cleared.MaxVal, Is.EqualTo(0.8).Within(1e-3));
            Assert.That(cleared.BlipTime, Is.EqualTo(500), "blip travels in milliseconds, not the seconds M106 B takes");
        });
    }

    /// <summary>M106 H without S defaults the fan to full speed</summary>
    /// <remarks>
    /// Fan::Configure (Fan.cpp): when H leaves sensorsMonitored non-empty, val defaults to 1.0 for
    /// safety, and no S was given to override it
    /// </remarks>
    [Test]
    public async Task M106ThermostaticDefaultsSpeedToFull()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config + $"\nM308 S1 P\"{Expansion.Temp(Machines.DriverBoard, 0)}\" Y\"thermistor\"");

        await bench.Host.ExecuteCodeAsync("M106 P0 H1 T45");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.EqualTo(1.0).Within(1e-3),
                    "M106 H without S defaults fans[0].requestedValue to full speed (Fan.cpp val = 1.0 for safety)");

        (_, CanMessageFanParameters sent) = bench.CanMaster.LastCanMessage<CanMessageFanParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.Val, Is.EqualTo(1.0).Within(1e-3),
                        "and the board is told the full speed too, so the fan runs whatever this side reports");
            Assert.That(sent.TriggerTemperatures[0], Is.EqualTo(45).Within(1e-3),
                        "a single T value pads to both temperatures on the way out (Fan.cpp GetFloatArray padding)");
            Assert.That(sent.TriggerTemperatures[1], Is.EqualTo(45).Within(1e-3));
        });
    }

    /// <summary>M106 C names the fan</summary>
    /// <remarks>Fan::Configure (Fan.cpp): C assigns the quoted string to the fan's name</remarks>
    [Test]
    public async Task M106NamesTheFan()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync("M106 P0 C\"PartCooler\"");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.Name), Is.EqualTo("PartCooler"),
                    "M106 C sets fans[0].name (Fan.cpp Fan::Configure, C parameter)");

        // The name is this side's alone: CanMessageFanParameters has no field for it, because a board
        // driving a PWM pin has no use for what the operator calls it. What C does send is the
        // parameters message every configuring M106 sends, carrying the fan's unchanged state
        (_, CanMessageFanParameters sent) = bench.CanMaster.LastCanMessage<CanMessageFanParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.FanNumber, Is.EqualTo(0));
            Assert.That(sent.Val, Is.Zero, "the fan was not asked to run");
            Assert.That(sent.SensorsMonitored, Is.Zero, "nor to watch anything");
        });
    }

    /// <summary>A bare M106 with no tool selected drives fan 0</summary>
    /// <remarks>
    /// GCodes2.cpp case 106 with no P calls SetMappedFanSpeed (GCodes.cpp), which drives fan 0 when
    /// no tool is current
    /// </remarks>
    [Test]
    public async Task BareM106WithoutToolDrivesFan0()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync("M106 S100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.EqualTo(100.0 / 255.0).Within(1e-3),
                    "bare M106 S100 with no tool drives fan 0: fans[0].requestedValue 100/255 (GCodes.cpp SetMappedFanSpeed)");

        (byte board, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(sent.FanNumber, Is.EqualTo(0), "the message names fan 0, which is what a code with no P "
                        + "addresses while no tool is selected (GCodes.cpp SetMappedFanSpeed)");
            Assert.That(sent.Pwm, Is.EqualTo(100.0f / 255.0f).Within(1e-3), "carrying the scaled speed");
        });
    }

    /// <summary>
    /// With a tool selected, a bare M106 or M107 addresses the fans the tool maps with M563 F
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 106 with no P calls SetMappedFanSpeed (GCodes.cpp), which with a current
    /// tool calls Tool::SetFansPwm on the tool's fan mapping. The tool is created with
    /// M563 P0 D0 F1, so its mapping is fan 1
    /// </remarks>
    [Test]
    public async Task BareM106AddressesTheToolFan()
    {
        const int fanId = 1;
        await using Bench bench = await Bench.StartAsync(Machines.Xye, TwoFanConfig + $"\nM563 P0 D0 F{fanId}");

        await bench.Host.ExecuteCodeAsync("T0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.Zero,
                    "T0 selects tool 0: state.currentTool 0");

        await bench.Host.ExecuteCodeAsync("M106 S200");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[fanId]!.RequestedValue), Is.EqualTo(200.0 / 255.0).Within(1e-3),
                    "bare M106 S200 with tool 0 selected drives its mapped fan (GCodes2.cpp case 106, Tool::SetFansPwm)");

        (byte board, CanMessageSetFanSpeed driven) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(driven.FanNumber, Is.EqualTo(fanId),
                        "the message names the tool's mapped fan, not fan 0 (GCodes.cpp SetMappedFanSpeed with a "
                        + "current tool calls Tool::SetFansPwm)");
            Assert.That(driven.Pwm, Is.EqualTo(200.0f / 255.0f).Within(1e-3));
        });

        await bench.Host.ExecuteCodeAsync("M107");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[fanId]!.RequestedValue), Is.Zero,
                    "bare M107 turns the tool's mapped fan off (GCodes2.cpp case 107)");

        (_, CanMessageSetFanSpeed stopped) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(stopped.FanNumber, Is.EqualTo(fanId), "and M107 addresses the same mapped fan");
            Assert.That(stopped.Pwm, Is.Zero);
        });

        Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>().Any(m => m.Message.FanNumber == 0), Is.False,
                    "fan 0 was never addressed: with a tool selected the mapping is what a bare M106 follows");
    }

    /// <summary>
    /// M106 R refuses a restore point number past the ones a client can see, and drives no fan
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 106 reads R with GetLimitedUIValue against NumVisibleRestorePoints, which is
    /// 6. The regression case is <c>testcases/fans/error-missing-parameters-fans.yaml</c>
    /// </remarks>
    [Test]
    public async Task M106RefusesARestorePointPastTheLimit()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        string reply = await bench.Host.ExecuteCodeAsync($"M106 R{DuetControlServer.Motion.RestorePoint.NumVisible}");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "a restore point number at NumVisibleRestorePoints is refused (GCodes2.cpp case 106)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Is.Empty,
                        "and the refusal drove no fan");
        });
    }

    /// <summary>
    /// M106 against a fan no M950 has created is an error, whether the number is one a machine could
    /// have or one past MaxFans
    /// </summary>
    /// <remarks>
    /// M106 reads P with TryGetUIValue and does not range-check it, so both numbers take the same
    /// FindFan path rather than a parameter guard (GCodes2.cpp case 106, FansManager::ConfigureFan).
    /// The regression case is <c>testcases/fans/m106-error-unknown-fan.yaml</c>: an unconfigured fan
    /// accepted silently would hide a typo in a config.g until someone noticed the part cooling
    /// never came on
    /// </remarks>
    [Test]
    public async Task M106AgainstAFanThatDoesNotExistIsRefused()
    {
        await using Bench bench = await Bench.StartAsync();

        string legal = await bench.Host.ExecuteCodeAsync("M106 P9 S0.5");
        string past = await bench.Host.ExecuteCodeAsync($"M106 P{FanManager.MaxFans * 2} S0.5");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(legal, Does.StartWith("Error:"),
                        "a fan number no M950 has created is refused (FansManager.cpp ConfigureFan, FindFan)");
            Assert.That(past, Does.StartWith("Error:"),
                        "a fan number past MaxFans takes the same path, because M106 does not range-check P "
                        + "(GCodes2.cpp case 106)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans.Count), Is.Zero,
                        "and neither code added anything to fans[]");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Is.Empty,
                        "nor put a speed on the bus for a fan no board has been told about");
        });
    }

    /// <summary>
    /// I and F are the RRF2 spelling of inversion and PWM frequency, and RepRapFirmware 3 neither
    /// honours nor rejects them: the code falls through to the report branch and changes nothing
    /// </summary>
    /// <remarks>
    /// Fan::Configure (Fan.cpp) does not read I or F, so <c>seen</c> stays false and the line is
    /// answered as a bare query. The regression case
    /// <c>testcases/fans/m106-fan-invert-and-frequency.yaml</c> reads the fan back afterwards for
    /// exactly this reason: without the readback "accepted silently" and "applied" look the same.
    /// The RRF3 spelling is the '!' and Q of M950, which
    /// <see cref="M950AssignsAFanAnInvertedPort"/> covers
    /// </remarks>
    [Test]
    public async Task M106IgnoresTheObsoleteInvertAndFrequencyParameters()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        string inverted = await bench.Host.ExecuteCodeAsync("M106 P0 I1 F25000");
        await bench.Host.ExecuteCodeAsync("M106 P0 I0");

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(inverted, Does.Not.StartWith("Error:"),
                        "I and F are not rejected either: Fan::Configure does not know them, so the line is "
                        + "answered as a query (Fan.cpp)");
            Assert.That(fan!.Frequency, Is.EqualTo(500),
                        "F25000 left fans[0].frequency at what M950 Q set: the RRF2 frequency parameter does nothing");
            Assert.That(fan!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 3)}"),
                        "and I1 left fans[0].port un-inverted");
            Assert.That(fan!.RequestedValue, Is.Zero, "neither code was a speed");
        });

        Assert.Multiple(() =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageFanParameters>(), Is.Empty,
                        "nothing was configured, so no parameters message went out (Fan.cpp, seen stays false)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Is.Empty,
                        "nor a speed");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Fan>(), Is.Empty,
                        "nor a port assignment: the board still drives the pin the way M950 set it up");
        });
    }

    /// <summary>
    /// The three parameters that shape a fan's PWM rather than set it - L a minimum, X a maximum and
    /// B a starting blip - are carried to the board, and the speeds that follow go out as asked
    /// rather than clamped on this side
    /// </summary>
    /// <remarks>
    /// LocalFan::InternalRefresh applies the floor and the ceiling on whichever board owns the fan,
    /// so what crosses CAN is the raw request plus a fanParameters message carrying the limits. The
    /// regression case <c>testcases/fans/m106-fan-pwm-limits.yaml</c> records exactly that: 0.05
    /// below a 20% floor and 1.0 above an 80% ceiling both reach the board unchanged
    /// </remarks>
    [Test]
    public async Task M106CarriesThePwmLimitsAndLeavesTheClampingToTheBoard()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync("M106 P0 L0.2 X0.8 B0.2");
        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Min, Is.EqualTo(0.2).Within(1e-3), "M106 L0.2 sets fans[0].min (Fan.cpp minVal)");
            Assert.That(fan!.Max, Is.EqualTo(0.8).Within(1e-3), "M106 X0.8 sets fans[0].max (Fan.cpp maxVal)");
            Assert.That(fan!.Blip, Is.EqualTo(0.2).Within(1e-3), "M106 B0.2 sets fans[0].blip in seconds (Fan.cpp blipTime)");
        });

        (byte board, CanMessageFanParameters limits) = bench.CanMaster.LastCanMessage<CanMessageFanParameters>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the limits go to the board that applies them");
            Assert.That(limits.FanNumber, Is.EqualTo(0));
            Assert.That(limits.MinVal, Is.EqualTo(0.2).Within(1e-3), "L");
            Assert.That(limits.MaxVal, Is.EqualTo(0.8).Within(1e-3), "X");
            Assert.That(limits.BlipTime, Is.EqualTo(200), "and B0.2 as 200 ms");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M106 P0 S0.05");
        await bench.Host.ExecuteCodeAsync("M106 P0 S1.0");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.EqualTo(1.0).Within(1e-3),
                        "fans[0].requestedValue is what was asked for, not what the fan will run at");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>()
                             .Select(sent => (sent.Board, sent.Message.FanNumber, Pwm: MathF.Round(sent.Message.Pwm, 3))),
                        Is.EqualTo(new (byte, ushort, float)[]
                        {
                            (Machines.DriverBoard, 0, 0.05f),
                            (Machines.DriverBoard, 0, 1.0f)
                        }),
                        "both speeds reach the board as the raw request: the 20% floor and the 80% ceiling are "
                        + "applied where the PWM is generated (LocalFan::InternalRefresh)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageFanParameters>(), Is.Empty,
                        "and a bare S sends no parameters message, because Fan::Configure acts on S only "
                        + "alongside another parameter (Fan.cpp)");
        });
    }

    /// <summary>
    /// M106 R restores the speed a G60 restore point saved, and only when the code names no fan
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 106 guards R with <c>gb.Seen('R') &amp;&amp; !seenFanNum</c>, so
    /// <c>M106 P0 R3</c> is silently ignored: Fan::Configure does not read R either. The regression
    /// case is <c>testcases/fans/m106-fan-name-and-restore.yaml</c>. R restoring nothing would mean a
    /// resumed print restarting with the fan off, since that is what resume.g uses it for
    /// </remarks>
    [Test]
    public async Task M106RRestoresTheSpeedARestorePointSaved()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        // No tool is selected, so the mapped fan is fan 0 and the speed is recorded in the movement
        // state's virtual fan speed, which is what G60 copies into the restore point
        await bench.Host.ExecuteCodeAsync("M106 S0.4");
        await bench.Host.ExecuteCodeAsync("G60 S3");
        await bench.Host.ExecuteCodeAsync("M106 S0");

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
#pragma warning disable CS0618
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[3].FanPwm), Is.EqualTo(0.4).Within(1e-3),
                    "G60 S3 saves the mapped fan speed in state.restorePoints[3].fanPwm (GCodes.cpp SavePosition, "
                    + "RestorePoint.cpp)");
#pragma warning restore
        Assert.That(fan!.RequestedValue, Is.Zero, "and M106 S0 turned the fan off, so the restore has something to undo");

        await bench.Host.ExecuteCodeAsync("M106 R3");
        Assert.That(fan!.RequestedValue, Is.EqualTo(0.4).Within(1e-3),
                    "M106 R3 puts the saved speed back on the mapped fan: fans[0].requestedValue 0.4 "
                    + "(GCodes2.cpp case 106, R parameter)");

        (byte board, CanMessageSetFanSpeed restored) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(restored.FanNumber, Is.EqualTo(0), "the restore reaches the mapped fan");
            Assert.That(restored.Pwm, Is.EqualTo(0.4f).Within(1e-3),
                        "carrying the saved speed, so the fan spins back up rather than the object model alone "
                        + "saying it did");
        });

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M106 P0 S0");
        await bench.Host.ExecuteCodeAsync("M106 P0 R3");
        Assert.Multiple(() =>
        {
            Assert.That(fan!.RequestedValue, Is.Zero,
                        "M106 P0 R3 is ignored: R is read only when no fan was named (GCodes2.cpp case 106)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Has.Count.EqualTo(1),
                        "so the only speed that followed was the M106 P0 S0, and the ignored R drove nothing");
        });
    }

    /// <summary>
    /// M106 with a fan number and nothing to act on reports the fan's whole configuration: its name,
    /// its speed, its limits and its blip time
    /// </summary>
    /// <remarks>
    /// Fan::Configure (Fan.cpp) reports when no parameter was seen and there is no R and no S, and
    /// the format is its. The regression case is <c>testcases/fans/m106-report-fan.yaml</c>, whose
    /// body only reads: a field quietly dropping out of the report is the regression it catches
    /// </remarks>
    [Test]
    public async Task M106ReportsTheFanConfiguration()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config + "\nM106 P0 L0.15 X0.9 B0.2 C\"drt test fan\"");

        string report = await bench.Host.ExecuteCodeAsync("M106 P0");
        Assert.Multiple(() =>
        {
            Assert.That(report.Trim(), Is.EqualTo("Fan 0 (drt test fan), speed 0%, min: 15%, max: 90%, blip: 0.20"),
                        "M106 P0 reports the fan's configuration in RepRapFirmware's format (Fan.cpp Fan::Configure, "
                        + "report branch)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageFanParameters>(), Is.Empty,
                        "a report changes nothing, so nothing goes to the board");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>(), Is.Empty);
        });
    }

    /// <summary>
    /// A thermostatic fan reports the sensors it watches and the temperatures it switches between,
    /// in place of the speed a manual fan reports
    /// </summary>
    /// <remarks>
    /// Fan::Configure (Fan.cpp) prints the speed only while sensorsMonitored is empty, and appends
    /// the trigger temperatures, the sensor list and the PWM the fan is actually running at
    /// otherwise. That last figure is the board's, so it reads "unknown" until the board reports one
    /// (RemoteFan.cpp lastPwm), which on this bench is never. The regression case is
    /// <c>testcases/fans/m106-thermostatic-fan.yaml</c>
    /// </remarks>
    [Test]
    public async Task M106ReportsAThermostaticFansTriggerTemperatures()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config + $"\nM308 S1 P\"{Expansion.Temp(Machines.DriverBoard, 0)}\" Y\"thermistor\"\nM106 P0 H1 T45 X0.7");

        string report = await bench.Host.ExecuteCodeAsync("M106 P0");
        Assert.That(report.Trim(),
                    Is.EqualTo("Fan 0, min: 10%, max: 70%, blip: 0.10, temperature: 45.0:45.0C, sensors: 1, "
                               + "current speed: unknown"),
                    "a thermostatic fan reports its sensors and trigger temperatures instead of a requested speed "
                    + "(Fan.cpp Fan::Configure, report branch)");
    }

    /// <summary>
    /// An M106 with nothing to act on and no fan to report answers with nothing
    /// </summary>
    /// <remarks>
    /// A bare M106 never reaches Fan::Configure - GCodes2.cpp case 106 calls it only when P was seen
    /// - and has no S or R to act on either, so the reply is empty. The regression cases are
    /// <c>testcases/fans/m106-report-fan.yaml</c>, where the blank reply is the assertion, and
    /// <c>testcases/fans/m106-fan-name-and-restore.yaml</c> for the R form
    /// </remarks>
    [Test]
    public async Task M106WithNothingToActOnRepliesWithNothing()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M106")).Trim(), Is.Empty,
                        "a bare M106 is a no-op rather than a report of fan 0: with no P, case 106 never calls "
                        + "ConfigureFan (GCodes2.cpp)");
            Assert.That((await bench.Host.ExecuteCodeAsync("M106 P0 R3")).Trim(), Is.Empty,
                        "and an M106 whose only other parameter is an R it will not read reports nothing either, "
                        + "because Fan::Configure reports only when there is no R (Fan.cpp)");
        });
    }

    /// <summary>
    /// A pause saves the last mapped fan speed in the pause restore point, and M106 R1 restores it
    /// </summary>
    /// <remarks>
    /// SetMappedFanSpeed (GCodes.cpp) records every mapped M106 S in ms.virtualFanSpeed; DoPause
    /// (GCodes.cpp) copies it into the pause restore point's fanSpeed, which the OM reports as
    /// state.restorePoints[].fanPwm (RestorePoint.cpp). GCodes2.cpp case 106 R feeds
    /// restorePoints[R].fanSpeed back through SetMappedFanSpeed. The job selects a tool whose
    /// M563 F mapping is fan 0, so the bare M106 addresses that fan
    /// </remarks>
    [Test]
    public async Task PauseSavesFanSpeedAndM106RRestoresIt()
    {
        await using Bench bench = await Bench.With(Machines.Xye, Fan0Config + "\nM563 P0 D0 F0")
                                             .WithSd(sd => sd.WriteGCode("job.gcode", "T0\nM106 S0.75\n" + JobDriving.FillerMoves()))
                                             .StartAsync();


        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Fans.Count > 0 && bench.Host.Model.Fans[0] is { RequestedValue: > 0.74f and < 0.76f },
            what: "the job's M106 S0.75 reaching fans[0]");

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);

#pragma warning disable CS0618
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[1].FanPwm), Is.EqualTo(0.75).Within(1e-3),
                    "the pause saves the mapped fan speed in state.restorePoints[1].fanPwm (GCodes.cpp DoPause, RestorePoint.cpp)");
#pragma warning restore

        await bench.Host.ExecuteCodeAsync("M107");
        Assert.That(fan!.RequestedValue, Is.Zero,
                    "M107 turns the mapped fan off before the restore: fans[0].requestedValue 0");

        await bench.Host.ExecuteCodeAsync("M106 R1");
        Assert.That(fan!.RequestedValue, Is.EqualTo(0.75).Within(1e-3),
                    "M106 R1 restores the saved speed to the mapped fan: fans[0].requestedValue 0.75 (GCodes2.cpp case 106, R parameter)");

        (byte board, CanMessageSetFanSpeed restored) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(restored.FanNumber, Is.EqualTo(0), "the restore reaches the tool's mapped fan");
            Assert.That(restored.Pwm, Is.EqualTo(0.75f).Within(1e-3),
                        "carrying the speed the restore point saved, so the fan actually spins back up rather "
                        + "than the object model alone saying it did");
        });

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>
    /// A board that takes a fan speed but warns about it has its warning reported, not dropped
    /// </summary>
    /// <remarks>
    /// GCodeResult::warning means the board did what it was asked and had something to say about it,
    /// and RepRapFirmware reports both halves (GCodes2.cpp HandleResult). A warning is the only sign
    /// the user gets that a fan is not doing quite what the code asked, so a handler that returns
    /// nothing unless the reply was an error loses it outright
    /// </remarks>
    [Test]
    public async Task M106ReportsABoardWarningAboutTheSpeed()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);
        bench.CanMaster.ReportWith<CanMessageSetFanSpeed>("fan 0 is thermostatic, speed ignored",
                                                          CodeResult.Warning);

        string reply = (await bench.Host.ExecuteCodeAsync("M106 P0 S0.5")).TrimEnd();
        Assert.That(reply, Is.EqualTo("Warning: M106: fan 0 is thermostatic, speed ignored"),
                    "the board took the speed and warned about it, and the warning is the code's result");
    }

    /// <summary>
    /// A board that takes a fan's configuration but warns about it has its warning reported
    /// </summary>
    /// <remarks>RemoteFan::UpdateFanConfiguration's reply, which carries a result code like any other</remarks>
    [Test]
    public async Task M106ReportsABoardWarningAboutTheConfiguration()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);
        bench.CanMaster.ReportWith<CanMessageFanParameters>("minimum PWM raised to the board's floor",
                                                            CodeResult.Warning);

        string reply = (await bench.Host.ExecuteCodeAsync("M106 P0 L0.25")).TrimEnd();
        Assert.That(reply, Is.EqualTo("Warning: M106: minimum PWM raised to the board's floor"),
                    "M106's configuration form carries the board's warning back the same way");
    }
}
