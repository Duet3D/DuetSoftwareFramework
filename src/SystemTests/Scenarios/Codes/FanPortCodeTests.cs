using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// <para>
/// The fan, general-purpose output and servo codes, asserted against the object model state and the
/// CAN messages RepRapFirmware gives each of them: M950 (F, P and S forms), M106 with its
/// thermostatic and scaling parameters, M107, M42 and M280.
/// </para>
/// <para>
/// Both halves are asserted because the board is what drives the pin. A code that writes the object
/// model and puts the wrong message on the bus leaves a machine that reads correctly and behaves
/// wrongly, and the model is the half a test reaches for first, so the message is where that goes
/// unnoticed. Which message each code sends is RepRapFirmware's: a speed is
/// <c>CanMessageSetFanSpeed</c> (RemoteFan::Refresh), the configuration is
/// <c>CanMessageFanParameters</c> carrying the fan's whole state (RemoteFan::UpdateFanConfiguration),
/// creating a port is an <c>M950</c> generic stating the configuration in full, defaults included
/// (RemoteFan::ConfigurePort, GpOutputPort::Configure), and changing one that exists carries only
/// what changed.
/// </para>
/// <para>
/// RepRapFirmware keeps servos and general-purpose outputs in one array: Platform::ConfigurePort
/// (Platform.cpp) routes both M950 P and M950 S to <c>gpoutPorts[number]</c>, differing only in the
/// servo flag. A servo therefore has no object model home of its own; M280 reports through
/// <c>state.gpOut[].pwm</c> as the duty cycle its pulse width amounts to (GpOutPort.cpp,
/// <c>WriteAnalog</c> setting <c>lastPwm</c> and the OBJECT_MODEL table reporting it). Because the
/// two forms share the index space, the gpout and servo tests here use separate benches so each
/// port keeps index 0.
/// </para>
/// <para>
/// <c>fans[].port</c> and <c>state.gpOut[].port</c> are deliberate DSF additions, asserted per
/// rrf-differences.md section 3: the object model has to hold enough to recreate the machine, and
/// without the port string nothing says which board carries the device.
/// </para>
/// </summary>
[TestFixture]
public class FanPortCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>CAN address of the board the bench puts every fan and port on</summary>
    private const byte Board = 1;

    /// <summary>Fan 0 on board 1 with an explicit PWM frequency</summary>
    private const string Fan0Config = "M950 F0 C\"1.out3\" Q500";

    /// <summary>Fans 0 and 1 on board 1, for the codes whose fan mapping matters</summary>
    private const string TwoFanConfig = Fan0Config + "\nM950 F1 C\"1.out6\"";

    /// <summary>
    /// M950 F creates the fan with RepRapFirmware's defaults around the parameters it was given
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) creates the fan and applies Q; the other
    /// values are the Fan constructor's defaults (Fan.cpp): min DefaultMinFanPwm = 0.1, max 1.0,
    /// blip DefaultFanBlipTime = 100 ms reported as 0.1 s, no name, no monitored sensors. The fan
    /// lives on a CAN board, so actualValue and rpm are RemoteFan's (RemoteFan.cpp): -1 until the
    /// board sends a fan report, and the bench's fake board never does. The constants are in CANlib
    /// RRF3Common.h
    /// </remarks>
    [Test]
    public async Task M950CreatesFanWithRrfDefaults()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: Fan0Config);

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Frequency, Is.EqualTo(500),
                        "M950 F0 Q500 sets fans[0].frequency (Hz, FansManager::ConfigureFanPort)");
            Assert.That(fan!.RequestedValue, Is.Zero,
                        "a new fan starts with fans[0].requestedValue 0 (Fan.cpp constructor)");
            Assert.That(fan!.ActualValue, Is.EqualTo(-1),
                        "fans[0].actualValue is -1 until the board reports a PWM (RemoteFan.cpp lastPwm)");
            Assert.That(fan!.Rpm, Is.EqualTo(-1),
                        "fans[0].rpm is -1 until the board reports a tacho reading (RemoteFan.cpp lastRpm)");
            Assert.That(fan!.Min, Is.EqualTo(0.1).Within(1e-3),
                        "fans[0].min defaults to DefaultMinFanPwm 0.1 (Fan.cpp constructor, RRF3Common.h)");
            Assert.That(fan!.Max, Is.EqualTo(1.0).Within(1e-3),
                        "fans[0].max defaults to 1.0 (Fan.cpp constructor)");
            Assert.That(fan!.Blip, Is.EqualTo(0.1).Within(1e-3),
                        "fans[0].blip defaults to 0.1 s (Fan.cpp constructor, DefaultFanBlipTime 100 ms)");
            Assert.That(fan!.Name, Is.Empty,
                        "a new fan has no fans[0].name (Fan.cpp constructor)");
            Assert.That(fan!.Thermostatic.Sensors.Count, Is.Zero,
                        "a new fan monitors no sensors: fans[0].thermostatic.sensors is empty (Fan.cpp constructor)");
            Assert.That(fan!.Port, Is.EqualTo("1.out3"),
                        "M950 F0 C records fans[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board), "the M950 goes to the board named by the port");
            Assert.That(sent.F, Is.EqualTo(0), "F is the fan number, which is what the board will know it by");
            Assert.That(sent.C, Is.EqualTo("1.out3"), "C is the port, address and all (RemoteFan::ConfigurePort)");
            Assert.That(sent.Q, Is.EqualTo(500), "Q is the frequency the code asked for");
            Assert.That(sent.K, Is.EqualTo(2.0f).Within(1e-3),
                        "K is stated even though the code omitted it: RepRapFirmware passes "
                        + "DefaultFanTachoPulsesPerRev to ConfigurePort rather than leaving the board to guess");
        });
    }

    /// <summary>
    /// M950 F with no Q or K still tells the board what frequency and tacho scaling to use, rather
    /// than leaving it to a default of the board's own
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) resolves both before it sends anything -
    /// DefaultFanPwmFreq 250 Hz and DefaultFanTachoPulsesPerRev 2 (CANlib RRF3Common.h) - and
    /// RemoteFan::ConfigurePort puts all four parameters in the message every time. A board left to
    /// pick its own default would run the fan at a frequency fans[0].frequency does not report
    /// </remarks>
    [Test]
    public async Task M950StatesTheFanDefaultsItDidNotReceive()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 F0 C\"1.out3\"");

        (_, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(async () =>
        {
            Assert.That(sent.Q, Is.EqualTo(250), "Q defaults to DefaultFanPwmFreq (CANlib RRF3Common.h)");
            Assert.That(sent.K, Is.EqualTo(2.0f).Within(1e-3),
                        "K defaults to DefaultFanTachoPulsesPerRev (CANlib RRF3Common.h)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.Frequency), Is.EqualTo(250),
                        "and fans[0].frequency reports the same figure the board was given");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr), Is.EqualTo(2.0).Within(1e-3),
                        "as does fans[0].tachoPpr");
        });
    }

    /// <summary>M950 F with Q and no C changes only the frequency of an existing fan</summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) falls through to
    /// RemoteFan::SetFanParameters when no pin name is given
    /// </remarks>
    [Test]
    public async Task M950SetsFanFrequencyWithoutPort()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: Fan0Config);

        await bench.Host.ExecuteCodeAsync("M950 F0 Q100");
        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Frequency, Is.EqualTo(100),
                        "M950 F0 Q100 with no C changes fans[0].frequency (RemoteFan::SetFanParameters)");
            Assert.That(fan!.Port, Is.EqualTo("1.out3"),
                        "M950 F0 C records fans[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board), "the change goes to the board the fan is already on");
            Assert.That(sent.F, Is.EqualTo(0), "F names the fan being changed");
            Assert.That(sent.Q, Is.EqualTo(100), "Q is the new frequency");
            Assert.That(sent.C, Is.Null,
                        "and no C: RemoteFan::SetFanParameters carries only what changed, so the board's "
                        + "port assignment is left alone rather than reassigned to itself");
            Assert.That(sent.K, Is.Null, "nor K, which this code did not touch");
        });
    }

    /// <summary>
    /// M950 K is clamped to the range a tacho can be read over, and the board is told the clamped
    /// figure rather than what the operator wrote
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) reads K with GetLimitedFValue between
    /// MinFanPulsesPerRev 0.5 and MaxFanPulsesPerRev 200 (CANlib RRF3Common.h). RepRapFirmware
    /// refuses a value outside them where DSF clamps, which is what every other limited parameter
    /// does here, but either way the board and fans[0].tachoPpr have to end up holding the same
    /// number: the board scales the pulses it counts by its copy to reach the RPM it reports back
    /// </remarks>
    [Test]
    public async Task M950ClampsTachoPulsesPerRev()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M950 F0 C\"1.out3\" K0.1");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr),
                        Is.EqualTo(Fan.MinTachoPpr).Within(1e-3),
                        "M950 K below the minimum clamps fans[0].tachoPpr (CANlib MinFanPulsesPerRev)");
            Assert.That(SentTachoPpr(bench), Is.EqualTo(Fan.MinTachoPpr).Within(1e-3),
                        "and the M950 the board is sent carries the clamped value, not the 0.1 that was written");
        });

        await bench.Host.ExecuteCodeAsync("M950 F0 K1000");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr),
                        Is.EqualTo(Fan.MaxTachoPpr).Within(1e-3),
                        "M950 K above the maximum clamps fans[0].tachoPpr (CANlib MaxFanPulsesPerRev)");
            Assert.That(SentTachoPpr(bench), Is.EqualTo(Fan.MaxTachoPpr).Within(1e-3),
                        "and the frequency-only form clamps what it sends too");
        });

        await bench.Host.ExecuteCodeAsync("M950 F0 K4");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr), Is.EqualTo(4).Within(1e-3),
                        "a value inside the range is stored as written");
            Assert.That(SentTachoPpr(bench), Is.EqualTo(4).Within(1e-3), "and sent as written");
        });
    }

    /// <summary>The K parameter of the last M950 the machine sent a fan's board</summary>
    private static float? SentTachoPpr(JobBench bench) => bench.CanMaster.LastCanMessage<CanMessageM950Fan>().Message.K;

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoFanConfig);

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
                        (Board, 0, 0.5f),
                        (Board, 0, MathF.Round(128.0f / 255.0f, 3)),
                        (Board, 0, 1.0f),
                        (Board, 1, 0.75f)
                    }),
                    "one SetFanSpeed per M106 S, each naming its fan and the 0..1 value it scaled to "
                    + "(RemoteFan::Refresh)");
    }

    /// <summary>A bare M107 with no tool selected turns off fan 0 and only fan 0</summary>
    /// <remarks>
    /// GCodes2.cpp case 107 calls SetMappedFanSpeed(gb, 0.0), and SetMappedFanSpeed (GCodes.cpp)
    /// addresses fan 0 when no tool is selected
    /// </remarks>
    [Test]
    public async Task M107TurnsOffMappedFanWithoutTool()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoFanConfig);

        await bench.Host.ExecuteCodeAsync("M106 P0 S255");
        await bench.Host.ExecuteCodeAsync("M106 P1 S0.75");
        await bench.Host.ExecuteCodeAsync("M107");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.Zero,
                        "bare M107 with no tool turns off fan 0: fans[0].requestedValue 0 (GCodes.cpp SetMappedFanSpeed)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[1]!.RequestedValue), Is.EqualTo(0.75).Within(1e-3),
                        "bare M107 leaves fans[1].requestedValue alone (GCodes2.cpp case 107)");
        });

        (byte board, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
            Assert.That(sent.FanNumber, Is.EqualTo(0),
                        "the last speed on the bus is fan 0's: M107 addressed the mapped fan, which with no "
                        + "tool selected is fan 0 (GCodes.cpp SetMappedFanSpeed)");
            Assert.That(sent.Pwm, Is.Zero, "and asked for nothing");
            Assert.That(bench.CanMaster.CanMessages<CanMessageSetFanSpeed>().Count(m => m.Message.FanNumber == 1),
                        Is.EqualTo(1), "fan 1 heard only its own M106: M107 sent it nothing");
        });
    }

    /// <summary>M107 ignores its P parameter and still addresses the mapped fan</summary>
    /// <remarks>
    /// GCodes2.cpp case 107 reads no parameter at all: it is one call to
    /// SetMappedFanSpeed(gb, 0.0), so M107 P1 turns off fan 0 (no tool selected) and fan 1 keeps
    /// its speed
    /// </remarks>
    [Test]
    public async Task M107IgnoresPParameter()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: TwoFanConfig);

        await bench.Host.ExecuteCodeAsync("M106 P0 S255");
        await bench.Host.ExecuteCodeAsync("M106 P1 S0.75");
        await bench.Host.ExecuteCodeAsync("M107 P1");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.Zero,
                        "M107 P1 still turns off the mapped fan 0: fans[0].requestedValue 0 (GCodes2.cpp case 107 reads no P)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[1]!.RequestedValue), Is.EqualTo(0.75).Within(1e-3),
                        "M107 P1 leaves fans[1].requestedValue alone (GCodes2.cpp case 107 reads no P)");
        });

        (_, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(sent.FanNumber, Is.EqualTo(0),
                        "the P1 went nowhere: the message names fan 0, the mapped one");
            Assert.That(sent.Pwm, Is.Zero);
        });
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
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: Fan0Config + "\nM308 S1 P\"1.temp0\" Y\"thermistor\"");

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
            Assert.That(board, Is.EqualTo(Board), "the parameters go to the board that reads the sensors");
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
            Assert.That(clearedBoard, Is.EqualTo(Board));
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
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: Fan0Config + "\nM308 S1 P\"1.temp0\" Y\"thermistor\"");

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: Fan0Config);

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
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: Fan0Config);

        await bench.Host.ExecuteCodeAsync("M106 S100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.RequestedValue), Is.EqualTo(100.0 / 255.0).Within(1e-3),
                    "bare M106 S100 with no tool drives fan 0: fans[0].requestedValue 100/255 (GCodes.cpp SetMappedFanSpeed)");

        (byte board, CanMessageSetFanSpeed sent) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
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
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: TwoFanConfig + $"\nM563 P0 D0 F{fanId}");

        await bench.Host.ExecuteCodeAsync("T0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.Zero,
                    "T0 selects tool 0: state.currentTool 0");

        await bench.Host.ExecuteCodeAsync("M106 S200");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[fanId]!.RequestedValue), Is.EqualTo(200.0 / 255.0).Within(1e-3),
                    "bare M106 S200 with tool 0 selected drives its mapped fan (GCodes2.cpp case 106, Tool::SetFansPwm)");

        (byte board, CanMessageSetFanSpeed driven) = bench.CanMaster.LastCanMessage<CanMessageSetFanSpeed>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
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
    /// M950 P creates a general-purpose output at the default M42 frequency, off, with its port
    /// recorded
    /// </summary>
    /// <remarks>
    /// GpOutputPort::Configure (GpOutPort.cpp): with C and no Q the frequency is
    /// DefaultPinWritePwmFreq = 500 Hz for the non-servo form (RRF3Common.h). The OM table
    /// (GpOutPort.cpp) reports freq and pwm
    /// </remarks>
    [Test]
    public async Task M950CreatesGpOutPortWithDefaultFrequency()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 P0 C\"1.out4\"");

        GpOutputPort? gpOutPort = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(gpOutPort!.Freq, Is.EqualTo(500),
                        "M950 P0 without Q defaults state.gpOut[0].freq to DefaultPinWritePwmFreq 500 (GpOutPort.cpp Configure)");
            Assert.That(gpOutPort!.Pwm, Is.Zero,
                        "a new output starts with state.gpOut[0].pwm 0");
            Assert.That(gpOutPort!.Port, Is.EqualTo("1.out4"),
                        "M950 P0 C records state.gpOut[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
            Assert.That(sent.P, Is.EqualTo(0), "P is the port number the board will know it by");
            Assert.That(sent.C, Is.EqualTo("1.out4"), "C is the port");
            Assert.That(sent.Q, Is.EqualTo(500),
                        "Q states DefaultPinWritePwmFreq rather than leaving the board to default it "
                        + "(GpOutPort.cpp Configure resolves the frequency before it sends anything)");
            Assert.That(sent.S, Is.Zero, "S is the servo flag, and this is a plain output");
        });
    }

    /// <summary>M42 S scales its value into state.gpOut[].pwm the way a fan speed scales</summary>
    /// <remarks>
    /// GCodes2.cpp case 42 reads S with GetPwmValue (GCodeBuffer.cpp: above 1 is out of 255,
    /// constrained to 0..1) and GpOutputPort::WriteAnalog stores it in lastPwm, which the OM table
    /// reports as state.gpOut[].pwm
    /// </remarks>
    [Test]
    public async Task M42DrivesGpOutPwm()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 P0 C\"1.out4\"");

        GpOutputPort? gpOut = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);

        await bench.Host.ExecuteCodeAsync("M42 P0 S0.5");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.5).Within(1e-3),
                    "M42 S0.5 sets state.gpOut[0].pwm as a fraction (GCodes2.cpp case 42, GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S255");
        Assert.That(gpOut!.Pwm, Is.EqualTo(1.0).Within(1e-3),
                    "M42 S255 sets state.gpOut[0].pwm to 1 (GetPwmValue scales out of 255)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S128");
        Assert.That(gpOut!.Pwm, Is.EqualTo(128.0 / 255.0).Within(1e-3),
                    "M42 S128 sets state.gpOut[0].pwm to 128/255 (GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S-3");
        Assert.That(gpOut!.Pwm, Is.Zero,
                    "M42 with a negative S constrains state.gpOut[0].pwm to 0 (GetPwmValue constrains 0..1)");

        // Every write reached the board as a WriteGpio naming the port and the duty cycle, scaled on
        // this side: CanInterface::WriteGpio carries the port number, the PWM and the servo flag
        Assert.That(bench.CanMaster.CanMessages<CanMessageWriteGpio>()
                         .Select(w => (w.Board, w.Message.PortNumber, w.Message.IsServo, Pwm: MathF.Round(w.Message.Pwm, 3))),
                    Is.EqualTo(new (byte, byte, bool, float)[]
                    {
                        (Board, 0, false, 0.5f),
                        (Board, 0, false, 1.0f),
                        (Board, 0, false, MathF.Round(128.0f / 255.0f, 3)),
                        (Board, 0, false, 0.0f)
                    }),
                    "one WriteGpio per M42, none of them flagged as a servo (GCodes2.cpp case 42)");
    }

    /// <summary>M950 P with Q and no C changes only the frequency of an existing output</summary>
    /// <remarks>GpOutputPort::Configure (GpOutPort.cpp), the frequency-only form</remarks>
    [Test]
    public async Task M950SetsGpOutFrequencyWithoutPort()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 P0 C\"1.out4\"");

        await bench.Host.ExecuteCodeAsync("M950 P0 Q100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Freq), Is.EqualTo(100),
                    "M950 P0 Q100 with no C changes state.gpOut[0].freq (GpOutPort.cpp Configure, frequency-only form)");

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
            Assert.That(sent.P, Is.EqualTo(0), "P names the port being changed");
            Assert.That(sent.Q, Is.EqualTo(100), "Q is the new frequency");
            Assert.That(sent.C, Is.Null,
                        "and no C or S: the frequency-only branch sends those two alone, so the board's port "
                        + "assignment survives (GpOutPort.cpp Configure)");
            Assert.That(sent.S, Is.Null);
        });
    }

    /// <summary>M950 S creates a servo at the 50 Hz refresh frequency</summary>
    /// <remarks>
    /// GpOutputPort::Configure (GpOutPort.cpp): the servo form without Q uses
    /// DefaultServoRefreshFrequency = 50 Hz (RRF3Common.h)
    /// </remarks>
    [Test]
    public async Task M950CreatesServoAtRefreshFrequency()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 S0 C\"1.out5\"");

        Assert.That(await bench.Host.ReadModelAsync(model => model.State.GpOut[0]!.Freq), Is.EqualTo(50),
                    "M950 S0 without Q defaults state.gpOut[0].freq to DefaultServoRefreshFrequency 50 (GpOutPort.cpp Configure)");

        (byte board, CanMessageM950Gpio sent) = bench.CanMaster.LastCanMessage<CanMessageM950Gpio>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Board));
            Assert.That(sent.S, Is.EqualTo(1),
                        "S reaches the board as the servo flag, which is the whole difference between the two "
                        + "forms of M950 (Platform.cpp ConfigurePort routes both to gpoutPorts[])");
            Assert.That(sent.P, Is.EqualTo(0),
                        "and the servo number travels as P: the board reads P as the port number, so the number "
                        + "in an M950 S cannot be passed through as written");
            Assert.That(sent.Q, Is.EqualTo(50), "Q states DefaultServoRefreshFrequency");
            Assert.That(sent.C, Is.EqualTo("1.out5"));
        });
    }

    /// <summary>
    /// M280 converts its S value to a pulse width and reports the position through
    /// state.gpOut[].pwm as the duty cycle that width amounts to
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 280: an S below MinServoPulseWidth = 544 is an angle, converted as
    /// min(angle, 180) * (2400 - 544) / 180 + 544 microseconds (GCodes.h); 544 and above is a pulse
    /// width, clamped to MaxServoPulseWidth = 2400; negative disables with width 0. The stored
    /// value is width * 1e-6 * frequency (GpOutputPort::WriteAnalog into lastPwm), so at the servo
    /// default of 50 Hz an angle of 90 is 1472 us and pwm 0.0736
    /// </remarks>
    [Test]
    public async Task M280ReportsServoPositionAsGpOutPwm()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M950 S0 C\"1.out5\"");

        GpOutputPort? gpOut = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);

        await bench.Host.ExecuteCodeAsync("M280 P0 S90");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.0736).Within(1e-3),
                    "M280 S90 converts the angle to 1472 us and state.gpOut[0].pwm 0.0736 at 50 Hz (GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S1000");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.05).Within(1e-3),
                    "M280 S1000 is a pulse width in us: state.gpOut[0].pwm 0.05 at 50 Hz (GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S3000");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.12).Within(1e-3),
                    "M280 S3000 clamps to MaxServoPulseWidth 2400 us: state.gpOut[0].pwm 0.12 (GCodes.h, GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S-30");
        Assert.That(gpOut!.Pwm, Is.Zero,
                    "M280 with a negative S disables the servo: state.gpOut[0].pwm 0 (GCodes2.cpp case 280)");

        // The board is asked for a duty cycle, not an angle: the pulse width and the port's refresh
        // frequency are resolved here, which is why the frequency has to be kept on this side at all
        Assert.That(bench.CanMaster.CanMessages<CanMessageWriteGpio>()
                         .Select(w => (w.Board, w.Message.PortNumber, w.Message.IsServo, Pwm: MathF.Round(w.Message.Pwm, 4))),
                    Is.EqualTo(new (byte, byte, bool, float)[]
                    {
                        (Board, 0, true, 0.0736f),
                        (Board, 0, true, 0.05f),
                        (Board, 0, true, 0.12f),
                        (Board, 0, true, 0.0f)
                    }),
                    "one WriteGpio per M280, each flagged as a servo and carrying the duty cycle its pulse "
                    + "width amounts to at 50 Hz (GCodes2.cpp case 280, CanInterface::WriteGpio)");
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
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: Fan0Config + "\nM563 P0 D0 F0",
            prepareSd: sd => sd.WriteGCode("job.gcode", "T0\nM106 S0.75\n" + JobControlBench.FillerMoves()));


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
            Assert.That(board, Is.EqualTo(Board));
            Assert.That(restored.FanNumber, Is.EqualTo(0), "the restore reaches the tool's mapped fan");
            Assert.That(restored.Pwm, Is.EqualTo(0.75f).Within(1e-3),
                        "carrying the speed the restore point saved, so the fan actually spins back up rather "
                        + "than the object model alone saying it did");
        });

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }
}
