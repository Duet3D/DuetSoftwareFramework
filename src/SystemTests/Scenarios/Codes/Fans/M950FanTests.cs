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

/// <summary>M950 F, creating, configuring and deleting a fan</summary>
[TestFixture]
[GCode("M950")]
public class M950FanTests : BenchFixture
{
    /// <summary>The K parameter of the last M950 the machine sent a fan's board</summary>
    private static float? SentTachoPpr(Bench bench) => bench.CanMaster.LastCanMessage<CanMessageM950Fan>().Message.K;

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
        await using Bench bench = await Bench.With(Fan0Config).KeepingConfigTraffic().StartAsync();

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
            Assert.That(fan!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 3)}"),
                        "M950 F0 C records fans[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the M950 goes to the board named by the port");
            Assert.That(sent.F, Is.EqualTo(0), "F is the fan number, which is what the board will know it by");
            Assert.That(sent.C, Is.EqualTo("out3"),
                        "C is the pin as the board knows it, with the address taken off: a board addresses its "
                        + "own ports and has no reader for one (CanMessageGenericConstructor.cpp, reducedString)");
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
        await using Bench bench = await Bench.With($"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}\"")
                                             .KeepingConfigTraffic()
                                             .StartAsync();

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
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync("M950 F0 Q100");
        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Frequency, Is.EqualTo(100),
                        "M950 F0 Q100 with no C changes fans[0].frequency (RemoteFan::SetFanParameters)");
            Assert.That(fan!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 3)}"),
                        "M950 F0 C records fans[0].port (DSF addition, rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the change goes to the board the fan is already on");
            Assert.That(sent.F, Is.EqualTo(0), "F names the fan being changed");
            Assert.That(sent.Q, Is.EqualTo(100), "Q is the new frequency");
            Assert.That(sent.C, Is.Null,
                        "and no C: RemoteFan::SetFanParameters carries only what changed, so the board's "
                        + "port assignment is left alone rather than reassigned to itself");
            Assert.That(sent.K, Is.Null, "nor K, which this code did not touch");
        });
    }

    /// <summary>
    /// M950 K outside the range a tacho can be read over is refused, and the fan that holds the
    /// number is left as it was
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) reads K with GetLimitedFValue between
    /// MinFanPulsesPerRev 0.5 and MaxFanPulsesPerRev 200 (CANlib RRF3Common.h), which throws rather
    /// than clamping: the board scales the pulses it counts by its copy to reach the RPM it reports
    /// back, so a figure nobody wrote would be reported as a speed nobody asked for. K is read
    /// ahead of the C that deletes the old fan, so the refusal costs the machine nothing
    /// </remarks>
    [Test]
    public async Task M950RefusesTachoPulsesPerRevOutsideItsRange()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync($"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}\" K4");
        bench.CanMaster.ClearCapture();

        string tooLow = (await bench.Host.ExecuteCodeAsync($"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\" K0.1")).TrimEnd();
        string tooHigh = (await bench.Host.ExecuteCodeAsync("M950 F0 K1000")).TrimEnd();
        Assert.Multiple(() =>
        {
            Assert.That(tooLow, Is.EqualTo("Error: at column 20: M950: parameter 'K' too low"),
                        "K below the minimum is refused where it stood (CANlib MinFanPulsesPerRev)");
            Assert.That(tooHigh, Is.EqualTo("Error: at column 10: M950: parameter 'K' too high"),
                        "and K above the maximum too (CANlib MaxFanPulsesPerRev)");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Fan>(), Is.Empty,
                        "neither refusal reaches a board, so the fan keeps the port it was given");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr), Is.EqualTo(4).Within(1e-3),
                        "and fans[0].tachoPpr still holds what the last code that was taken asked for");
        });

        await bench.Host.ExecuteCodeAsync("M950 F0 K8");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]!.TachoPpr), Is.EqualTo(8).Within(1e-3),
                        "a value inside the range is stored as written");
            Assert.That(SentTachoPpr(bench), Is.EqualTo(8).Within(1e-3), "and sent as written");
        });
    }

    /// <summary>
    /// M950 refuses a fan number past the limit before it looks at the port, so a typo claims no pin
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) reads F with GetLimitedUIValue against
    /// MaxFans, which throws before C is read. The regression case
    /// <c>testcases/fans/error-missing-parameters-fans.yaml</c> records the rejection with no CAN
    /// traffic: the pin named by a refused line stays free for whatever asks for it next
    /// </remarks>
    [Test]
    public async Task M950RefusesAFanNumberPastTheLimit()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M950 F{FanManager.MaxFans} C\"{Expansion.Out(Machines.DriverBoard, 3)}\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"),
                        "a fan number at MaxFans is refused (FansManager.cpp ConfigureFanPort, GetLimitedUIValue)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans.Count), Is.Zero,
                        "and fans[] is left as it was");
            Assert.That(bench.CanMaster.CanMessages<CanMessageM950Fan>(), Is.Empty,
                        "and no board was asked to claim the port the refused line named");
        });
    }

    /// <summary>
    /// The RRF3 spelling of an inverted fan output is a '!' in front of the M950 port name, and the
    /// modifier belongs to the pin rather than to the board address
    /// </summary>
    /// <remarks>
    /// IoPort::RemoveBoardAddress reads the modifiers before the address, so <c>!1.out3</c> is board
    /// 1's <c>!out3</c>. The regression case
    /// <c>testcases/fans/m106-fan-invert-and-frequency.yaml</c> pairs it with Q25000, the usual
    /// frequency for a 4-wire PWM fan, so that the frequency has to be carried rather than being
    /// right by accident
    /// </remarks>
    [Test]
    public async Task M950AssignsAFanAnInvertedPort()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync($"M950 F0 C\"!{Expansion.Out(Machines.DriverBoard, 3)}\" Q25000");
        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Frequency, Is.EqualTo(25000),
                        "M950 Q25000 sets fans[0].frequency (FansManager.cpp ConfigureFanPort)");
            Assert.That(fan!.Port, Is.EqualTo("!1.out3"),
                        "fans[0].port keeps the '!' that says the output is inverted (DSF addition, "
                        + "rrf-differences.md section 3)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard),
                        "the address in front of the pin still says which board claims it, modifier or not "
                        + "(IoPort::RemoveBoardAddress)");
            Assert.That(sent.F, Is.EqualTo(0));
            Assert.That(sent.C, Is.EqualTo("!out3"),
                        "and the '!' travels with the port while the address does not: whether a pin is inverted "
                        + "is the board's business, and which board carries it is not");
            Assert.That(sent.Q, Is.EqualTo(25000), "carrying the new frequency");
        });
    }

    /// <summary>
    /// M950 F with a tacho input as the second half of C claims both ports on the same board, and K
    /// sets how many pulses a revolution makes
    /// </summary>
    /// <remarks>
    /// The CAN address at the front of the string applies to the second port too, which is the part
    /// most likely to break, and '^' enables the tacho input's pullup so a disconnected input sits
    /// high rather than floating. The regression case is
    /// <c>testcases/fans/m950-fan-with-tacho.yaml</c>. fans[0].rpm stays -1 here because the bench's
    /// board never sends a fan report, which is the same reason
    /// <see cref="M950CreatesFanWithRrfDefaults"/> gives (RemoteFan.cpp lastRpm)
    /// </remarks>
    [Test]
    public async Task M950CreatesAFanWithATachoInput()
    {
        await using Bench bench = await Bench.With($"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}+^io1.in\" Q100 K4")
                                             .KeepingConfigTraffic()
                                             .StartAsync();

        Fan? fan = await bench.Host.ReadModelAsync(model => model.Fans[0]);
        Assert.Multiple(() =>
        {
            Assert.That(fan!.Port, Is.EqualTo($"{Expansion.Out(Machines.DriverBoard, 3)}+^io1.in"),
                        "fans[0].port holds the pin pair as it was written, pullup and all (DSF addition, "
                        + "rrf-differences.md section 3)");
            Assert.That(fan!.Frequency, Is.EqualTo(100), "M950 Q100 sets fans[0].frequency");
            Assert.That(fan!.TachoPpr, Is.EqualTo(4.0).Within(1e-3),
                        "M950 K4 sets fans[0].tachoPpr rather than leaving it at DefaultFanTachoPulsesPerRev "
                        + "(FansManager.cpp ConfigureFanPort)");
            Assert.That(fan!.Rpm, Is.EqualTo(-1),
                        "fans[0].rpm is -1 until the board reports a tacho reading (RemoteFan.cpp lastRpm)");
        });

        (byte board, CanMessageM950Fan sent) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard),
                        "one address at the front of the string, and both pins belong to that board");
            Assert.That(sent.F, Is.EqualTo(0));
            Assert.That(sent.C, Is.EqualTo("out3+^io1.in"),
                        "both pins go to the board under the names it knows them by, the one address in front of "
                        + "the pair having said which board that is");
            Assert.That(sent.Q, Is.EqualTo(100));
            Assert.That(sent.K, Is.EqualTo(4.0f).Within(1e-3),
                        "and K is stated, because the board scales the pulses it counts by its copy to reach the "
                        + "RPM it reports back");
        });
    }

    /// <summary>
    /// M950 F C"nil" deletes a fan: it disappears from the object model and the board releases the
    /// pin, so the next M950 that wants it can have it
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) reads "nil" as a request to delete, and
    /// reporting a fan that is not there is an error rather than a description of the fan that used
    /// to be. The regression case is <c>testcases/fans/m950-fan-delete.yaml</c>: a delete that leaves
    /// the pin claimed makes the next M950 that wants it fail for a reason that has nothing to do
    /// with the line that hits it
    /// </remarks>
    [Test]
    public async Task M950DeletesAFanWithCNil()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        string deleted = await bench.Host.ExecuteCodeAsync("M950 F0 C\"nil\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(deleted, Does.Not.StartWith("Error:"), "C\"nil\" is a delete, not a bad port name");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]), Is.Null,
                        "the fan leaves fans[] (FansManager.cpp ConfigureFanPort)");
        });

        (byte board, CanMessageM950Fan released) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the release goes to the board that holds the pin");
            Assert.That(released.F, Is.EqualTo(0));
            Assert.That(released.C, Is.EqualTo("nil"),
                        "and says \"nil\", which is what frees the pin there rather than only here");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M950 F0"), Does.StartWith("Error:"),
                    "reporting a fan that has been deleted is an error, not a description of the fan that used "
                    + "to be there (FansManager.cpp ConfigureFanPort)");

        string reused = await bench.Host.ExecuteCodeAsync($"M950 F1 C\"{Expansion.Out(Machines.DriverBoard, 3)}\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reused, Does.Not.StartWith("Error:"),
                        "the pin really is free again: this only succeeds if the delete released it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[1]), Is.Not.Null,
                        "and fans[1] now holds the fan that took the pin over");
        });
    }

    /// <summary>
    /// M950 F with no other parameter asks the board which pins the fan drives, rather than
    /// describing them from the object model
    /// </summary>
    /// <remarks>
    /// RemoteFan::ReportPortDetails (RemoteFan.cpp) sends a bare M950 naming the fan and answers
    /// with whatever comes back. The board is asked because it holds the pin table and a pin usually
    /// has more than one name: the reference for <c>testcases/fans/m950-fan-with-tacho.yaml</c>
    /// records <c>tacho pin 1.(io1.in,serial1.rx)</c>, which nothing on this side could reconstruct
    /// from the <c>1.out3+^io1.in</c> the code was given. The bench's board acknowledges without
    /// composing a report, so what this asserts is the query, which is the part that belongs here
    /// </remarks>
    [Test]
    public async Task M950AsksTheBoardToReportTheFanPort()
    {
        await using Bench bench = await Bench.StartAsync($"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}\" Q100");

        await bench.Host.ExecuteCodeAsync("M950 F0");

        (byte board, CanMessageM950Fan query) = bench.CanMaster.LastCanMessage<CanMessageM950Fan>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the query goes to the board that drives the fan");
            Assert.That(query.F, Is.EqualTo(0), "naming the fan it is about");
            Assert.That(query.C, Is.Null,
                        "and nothing else: a C would reassign the port rather than ask about it "
                        + "(FansManager.cpp ConfigureFanPort branches on C)");
            Assert.That(query.Q, Is.Null, "nor a Q, which would change the frequency");
            Assert.That(query.K, Is.Null, "nor a K");
        });
    }

    /// <summary>
    /// A fan report for a fan the machine does not have is ignored, rather than creating one
    /// </summary>
    /// <remarks>
    /// FansManager::ProcessRemoteFanRpms (FansManager.cpp) looks the number up and does nothing when
    /// it is not there, and Heat::ProcessRemoteHeatersReport and Heat::ProcessRemoteSensorsReport
    /// read their reports the same way. A report says what a board is doing, not what the machine
    /// has. Creating an entry instead resurrects a device that was just deleted, because a board goes
    /// on reporting one for as long as it still holds it: found on the bench, where
    /// <c>M950 F0 C"nil"</c> put <c>fans[0]</c> back within a report interval as a fan with no port
    /// that nothing could drive
    /// </remarks>
    [Test]
    public async Task AFanReportDoesNotCreateTheFanItReportsOn()
    {
        await using Bench bench = await Bench.StartAsync(Fan0Config);

        await bench.Host.ExecuteCodeAsync("M950 F0 C\"nil\"");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]), Is.Null,
                    "the fan is gone before the board reports anything");

        // The board still holds the fan it was told to release until it acts on the message, so it
        // goes on reporting it. That report is what used to put the entry back
        bench.CanMaster.InjectFansReport(Machines.DriverBoard, fanNumber: 0, actualPwm: 0, rpm: -1);
        bench.CanMaster.InjectFansReport(Machines.DriverBoard, fanNumber: 3, actualPwm: 0.5f, rpm: 1200);
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.CompletedExchanges > 0,
                                             what: "the reports to be carried over the link");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans[0]), Is.Null,
                        "a report for the fan that was deleted does not bring it back");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Fans.Count), Is.LessThanOrEqualTo(1),
                        "and a report for a fan that was never created adds nothing to fans[]");
        });
    }
}
