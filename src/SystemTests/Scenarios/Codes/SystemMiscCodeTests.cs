using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// System, network and miscellaneous M-codes, asserted against RepRapFirmware's behaviour and the
/// object model fields each code must set.
/// <para>
/// Not covered here: M556 belongs to the kinematics fixture; M582 is not implemented in DSF and is
/// skipped; M997 (flashes firmware) and bare M999 (reboots DCS) are not executed. Bare M999 is
/// RepRapFirmware's recovery from the M112 halt (GCodes2.cpp case 999 calls SoftwareReset), so the
/// M112 test asserts the halted state only and leaves the reset to the reader.
/// </para>
/// </summary>
[TestFixture]
public class SystemMiscCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// Poll a macro run counter until it reaches the expected value, bounded so a macro that never
    /// runs fails the test naming the counter
    /// </summary>
    private static async Task WaitForGlobalAsync(DcsTestHost host, string name, int expected, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int value;
        do
        {
            value = await host.GlobalAsync(name);
            if (value == expected)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"global.{name} stayed {value}, expected {expected}");
    }

    /// <summary>
    /// M111 without parameters reports the debug state as a success message.
    /// </summary>
    /// <remarks>
    /// RRF RepRap::ProcessM111 (RepRap.cpp): with nothing to change it falls through to the debug
    /// report. There is no object model field for debug flags, so the reply is the contract
    /// </remarks>
    [Test]
    public async Task M111ReportsTheDebugState()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M111");
        Assert.That(reply.Trim(), Is.Not.Empty, "M111 without parameters reports the debug state (RRF RepRap::ProcessM111)");
        Assert.That(reply, Does.Not.Contain("Error"), "M111 without parameters is a report, not an error (RRF RepRap::ProcessM111)");
    }

    /// <summary>
    /// M112 performs the emergency stop: the stop crosses the link to the controller and the
    /// machine reports state.status halted until it is reset.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 112 calls DoEmergencyStop -> RepRap::EmergencyStop, and
    /// RepRap::GetStatusString reports "halted" while stopped (RepRap.cpp). The recovery is M999,
    /// which reboots DCS here and is therefore not executed; see the fixture doc comment
    /// </remarks>
    [Test]
    public async Task M112HaltsTheMachine()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        await bench.Host.ExecuteCodeAsync("M112");

        // The stop must reach the controller, which is what makes it an emergency stop rather than
        // a status change
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.EmergencyStop);

        await bench.Host.WaitForStatusAsync(MachineStatus.Halted);
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Assert.That(bench.Host.Model.State.Status, Is.EqualTo(MachineStatus.Halted),
                        "M112 sets state.status to halted (RRF RepRap::GetStatusString)");
        }
    }

    /// <summary>
    /// M115 reports the firmware name and version in RepRapFirmware's format, and the version it
    /// names is the one boards[0] reports.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 115: "FIRMWARE_NAME: %s FIRMWARE_VERSION: %s ELECTRONICS: %s". The
    /// same firmware describes itself in the object model as boards[0].firmwareName and
    /// boards[0].firmwareVersion (Platform.cpp objectModelTable)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M115ReportsTheFirmwareVersion()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M115");
        Assert.That(reply, Does.Contain("FIRMWARE_NAME:"), "M115 names the firmware (RRF GCodes2.cpp case 115)");
        Assert.That(reply, Does.Contain("FIRMWARE_VERSION:"), "M115 names the firmware version (RRF GCodes2.cpp case 115)");

        Match version = Regex.Match(reply, @"FIRMWARE_VERSION: (\S+)");
        Assert.That(version.Success, Is.True, "M115 reports a version token after FIRMWARE_VERSION:");
        Assert.That(await bench.Host.EvaluateRawAsync("boards[0].firmwareVersion"), Is.EqualTo(version.Groups[1].Value),
                    "M115's version matches boards[0].firmwareVersion (RRF Platform.cpp firmwareVersion)");
    }

    /// <summary>
    /// M118 routes its message to the requesting channel, so the reply carries the text.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 118: without P the type is GenericMessage and platform.Message sends
    /// the text to every destination, including the channel the code came from
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M118EchoesTheMessage()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M118 S\"M118 bench message\"");
        Assert.That(reply, Does.Contain("M118 bench message"),
                    "M118 S sends the message to the requesting channel (RRF GCodes2.cpp case 118, platform.Message with GenericMessage)");
    }

    /// <summary>
    /// M122 prints the diagnostics report, opening with RepRapFirmware's header and containing the
    /// motion section.
    /// </summary>
    /// <remarks>
    /// RRF RepRap::Diagnostics (RepRap.cpp) opens with "=== Diagnostics ===";
    /// Move::Diagnostics contributes the "=== Move ===" section
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M122ReportsDiagnostics()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M122");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain("=== Diagnostics ==="), "M122 opens with the diagnostics header (RRF RepRap::Diagnostics)");
            Assert.That(reply, Does.Contain("=== Move ==="), "M122 contains the Move section (RRF Move::Diagnostics)");
        });
    }

    /// <summary>
    /// M409 returns the queried key's live object model value as JSON, echoing the key and flags,
    /// with and without the F"v" verbose flag.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 409 answers {"key":...,"flags":...,"result":...} from
    /// ObjectModel::ReportAsJson; the result must equal what the expression evaluator reads for the
    /// same key. move.axes[0].homed is true here because config.g's G92 marked X homed
    /// </remarks>
    [Test]
    public async Task M409ReturnsTheLiveModelValue()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string homed = await bench.Host.EvaluateRawAsync("move.axes[0].homed");

        string reply = await bench.Host.ExecuteCodeAsync("M409 K\"move.axes[0].homed\"");
        using (JsonDocument document = JsonDocument.Parse(reply))
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("key").GetString(), Is.EqualTo("move.axes[0].homed"),
                            "M409 echoes the queried key (RRF GCodes2.cpp case 409)");
                Assert.That(document.RootElement.GetProperty("result").GetRawText(), Contains.Substring(homed),
                            "M409's result is the live value of move.axes[0].homed");
            });
        }

        string verboseReply = await bench.Host.ExecuteCodeAsync("M409 K\"move.axes[0].homed\" F\"v\"");
        using (JsonDocument document = JsonDocument.Parse(verboseReply))
        {
            Assert.Multiple(() =>
            {
                Assert.That(document.RootElement.GetProperty("flags").GetString(), Is.EqualTo("v"),
                            "M409 echoes the F flags (RRF GCodes2.cpp case 409)");
                Assert.That(document.RootElement.GetProperty("result").GetRawText(), Contains.Substring(homed),
                            "M409 F\"v\" still returns the live value of move.axes[0].homed");
            });
        }
    }

    /// <summary>
    /// M500 P31 writes the Z probe values to sys/config-override.g and M501 loads them back: the
    /// probe's trigger height survives the round trip and overwrites a live change.
    /// </summary>
    /// <remarks>
    /// RRF GCodes::WriteConfigOverrideFile calls Platform::WritePlatformParameters, which for P31
    /// writes every probe through ZProbe::WriteParameters (ZProbe.cpp) as
    /// "G31 K%u P%d [axis offsets] Z%.2f". GCodes2.cpp case 501 runs config-override.g as a macro,
    /// so the saved value replaces the live one; the trigger height is
    /// sensors.probes[].triggerHeight
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesConfigOverrideAndM501RestoresIt()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M558 K0 P8 C\"1.io1.in\"\n");
        await bench.Host.ExecuteCodeAsync("G31 K0 Z1.25");
        Assert.That(await bench.Host.EvaluateAsync("sensors.probes[0].triggerHeight"), Is.EqualTo(1.25).Within(1e-3),
                    "G31 K0 Z sets sensors.probes[0].triggerHeight");

        string reply = await bench.Host.ExecuteCodeAsync("M500 P31");
        Assert.That(reply, Does.Not.Contain("Error"), "M500 writes config-override.g without an error (RRF WriteConfigOverrideFile)");

        string overridePath = Path.Combine(bench.Host.Sd.Root, "sys", "config-override.g");
        Assert.That(File.Exists(overridePath), Is.True, "M500 creates sys/config-override.g (RRF WriteConfigOverrideFile)");
        Assert.That(await File.ReadAllTextAsync(overridePath), Does.Match(@"G31 K0 .*Z1\.25"),
                    "M500 P31 persists the probe's trigger height (RRF ZProbe::WriteParameters)");

        await bench.Host.ExecuteCodeAsync("G31 K0 Z3.5");
        Assert.That(await bench.Host.EvaluateAsync("sensors.probes[0].triggerHeight"), Is.EqualTo(3.5).Within(1e-3),
                    "the live trigger height moved before M501");

        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That(await bench.Host.EvaluateAsync("sensors.probes[0].triggerHeight"), Is.EqualTo(1.25).Within(1e-3),
                    "M501 restores sensors.probes[0].triggerHeight from config-override.g (RRF GCodes2.cpp case 501)");
    }

    /// <summary>
    /// M503 echoes the content of config.g.
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 503 reads CONFIG_FILE into the reply verbatim</remarks>
    [Test]
    public async Task M503EchoesConfigFile()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M503");
        Assert.That(reply, Does.Contain("M584 X1.0 Y1.1 E1.2"), "M503 echoes config.g's content (RRF GCodes2.cpp case 503)");
    }

    /// <summary>
    /// M505 reports and sets the system directory, which the object model carries as
    /// directories.system.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 505: without P it replies "Sys file path is %s"; with P it calls
    /// Platform::SetSysDir. The directory is reported as directories.system (RepRap.cpp
    /// objectModelTable, default "0:/sys/")
    /// </remarks>
    [Test]
    public async Task M505SetsTheSystemDirectory()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string omSys = await bench.Host.EvaluateRawAsync("directories.system");
        Assert.That(omSys, Is.EqualTo("0:/sys/"), "directories.system defaults to 0:/sys/ (RRF RepRap.cpp directories table)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505")).Trim(), Is.EqualTo($"Sys file path is {omSys}"),
                    "M505 without P reports directories.system (RRF GCodes2.cpp case 505)");

        await bench.Host.ExecuteCodeAsync("M470 P\"0:/sys2\"");
        string reply = await bench.Host.ExecuteCodeAsync("M505 P\"0:/sys2\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M505 P accepts an existing directory (RRF Platform::SetSysDir)");

        omSys = await bench.Host.EvaluateRawAsync("directories.system");
        Assert.That(omSys, Does.StartWith("0:/sys2"), "M505 P sets directories.system (RRF Platform::SetSysDir)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505")).Trim(), Is.EqualTo($"Sys file path is {omSys}"),
                    "M505's report follows directories.system after the change (RRF GCodes2.cpp case 505)");
    }

    /// <summary>
    /// M505.1 reports and sets the web directory, which the object model carries as
    /// directories.web.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 505 with command fraction 1: "HTTP file path is %s" and
    /// Platform::SetWebDir; directories.web defaults to "0:/www/" (RepRap.cpp)
    /// </remarks>
    [Test]
    public async Task M505Dot1SetsTheWebDirectory()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string omWeb = await bench.Host.EvaluateRawAsync("directories.web");
        Assert.That(omWeb, Is.EqualTo("0:/www/"), "directories.web defaults to 0:/www/ (RRF RepRap.cpp directories table)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505.1")).Trim(), Is.EqualTo($"HTTP file path is {omWeb}"),
                    "M505.1 without P reports directories.web (RRF GCodes2.cpp case 505)");

        await bench.Host.ExecuteCodeAsync("M470 P\"0:/www2\"");
        string reply = await bench.Host.ExecuteCodeAsync("M505.1 P\"0:/www2\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M505.1 P accepts an existing directory (RRF Platform::SetWebDir)");

        omWeb = await bench.Host.EvaluateRawAsync("directories.web");
        Assert.That(omWeb, Does.StartWith("0:/www2"), "M505.1 P sets directories.web (RRF Platform::SetWebDir)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M505.1")).Trim(), Is.EqualTo($"HTTP file path is {omWeb}"),
                    "M505.1's report follows directories.web after the change (RRF GCodes2.cpp case 505)");
    }

    /// <summary>
    /// M550 sets the machine name, which the object model reports as network.name, and reports it
    /// in RepRapFirmware's wording.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 550: P sets the name via reprap.SetName, no P replies
    /// "RepRap name: %s". The name is reported as network.name (Network.cpp objectModelTable,
    /// reprap.GetName()); RRF has no state.machineName
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M550SetsTheMachineName()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string omName = await bench.Host.EvaluateRawAsync("network.name");
        Assert.That((await bench.Host.ExecuteCodeAsync("M550")).Trim(), Is.EqualTo($"RepRap name: {omName}"),
                    "M550 without P reports network.name (RRF GCodes2.cpp case 550)");

        string reply = await bench.Host.ExecuteCodeAsync("M550 P\"Bench550\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M550 P accepts a new machine name (RRF reprap.SetName)");
        Assert.That(await bench.Host.EvaluateRawAsync("network.name"), Is.EqualTo("Bench550"),
                    "M550 P sets network.name (RRF Network.cpp name entry)");
    }

    /// <summary>
    /// M551 sets the password and reports nothing.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 551: RepRap::SetPassword, with no option to report it and no object
    /// model field, so the silent reply is the whole observable contract
    /// </remarks>
    [Test]
    public async Task M551AcceptsThePassword()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M551 P\"benchpass\"");
        Assert.That(reply.Trim(), Is.Empty, "M551 P sets the password without a report (RRF GCodes2.cpp case 551)");
    }

    /// <summary>
    /// M552 without parameters reports the network state.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 552 with no S or P calls Network::GetNetworkState, which replies
    /// "Network is %s, configured IP address: %s, actual IP address: %s" for an ethernet
    /// interface (for example W5500Interface::GetNetworkState). The interfaces themselves are
    /// network.interfaces[] (Network.cpp objectModelTable); rrf-differences.md documents no
    /// deviation for M552
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M552ReportsTheNetworkState()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M552");
        Assert.That(reply, Does.Match("Network is (enabled|disabled)"),
                    "M552 without parameters reports the network state (RRF Network::GetNetworkState)");
    }

    /// <summary>
    /// M581 with only a trigger number reports the trigger's configuration.
    /// </summary>
    /// <remarks>
    /// RRF TriggerItem::Configure (TriggerItem.cpp): with nothing to change it reports, and an
    /// unused trigger replies "Trigger n is not configured". RRF does not report triggers in the
    /// object model, so the report is the observable
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M581ReportsAnUnconfiguredTrigger()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M581 T2");
        Assert.That(reply.Trim(), Is.EqualTo("Trigger 2 is not configured"),
                    "M581 T2 without parameters reports the trigger (RRF TriggerItem::Configure report branch)");
    }

    /// <summary>
    /// M581.1 configures an expression trigger that runs trigger2.g when the expression becomes
    /// true, observed through a global the macro sets.
    /// </summary>
    /// <remarks>
    /// RRF TriggerItem::Configure command fraction 1 stores the expression and evaluates its
    /// initial value; GCodes::CheckTriggers fires trigger%u.g on the false-to-true transition
    /// (GCodes.cpp). RRF keeps no trigger state in the object model, so the macro's side effect is
    /// the assertion
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M581Dot1ExpressionTriggerRunsItsMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global fireTrigger = false\nglobal trigRan = 0\n",
            prepareSd: sd => sd.WriteSys("trigger2.g", "set global.trigRan = global.trigRan + 1\n"));

        string reply = await bench.Host.ExecuteCodeAsync("M581.1 T2 P\"global.fireTrigger\" R0");
        Assert.That(reply, Does.Not.Contain("Error"), "M581.1 accepts an expression trigger (RRF TriggerItem::Configure fraction 1)");

        await bench.Host.ExecuteCodeAsync("set global.fireTrigger = true");
        await WaitForGlobalAsync(bench.Host, "trigRan", 1);
        Assert.That(await bench.Host.GlobalAsync("trigRan"), Is.EqualTo(1),
                    "the expression's false-to-true transition ran trigger2.g once (RRF GCodes::CheckTriggers)");
    }

    /// <summary>
    /// M586 reports and sets the CORS site, which the object model carries as network.corsSite.
    /// </summary>
    /// <remarks>
    /// RRF Network::ConfigureNetworkProtocol (Network.cpp): C sets the site via SetCorsSite, and
    /// the report is "CORS enabled for site '%s'" or "CORS disabled". The site is network.corsSite
    /// (Network.cpp objectModelTable)
    /// </remarks>
    [Test]
    public async Task M586ConfiguresTheCorsSite()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS disabled"),
                    "M586 without C reports no CORS site (RRF Network::ConfigureNetworkProtocol)");

        await bench.Host.ExecuteCodeAsync("M586 C\"example.com\"");
        Assert.That(await bench.Host.EvaluateRawAsync("network.corsSite"), Is.EqualTo("example.com"),
                    "M586 C sets network.corsSite (RRF Network::SetCorsSite; OM Network.cpp corsSite)");
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS enabled for site 'example.com'"),
                    "M586's report names the configured site (RRF Network::ConfigureNetworkProtocol)");

        await bench.Host.ExecuteCodeAsync("M586 C\"\"");
        Assert.That(await bench.Host.ExecuteCodeAsync("M586"), Does.Contain("CORS disabled"),
                    "M586 C\"\" clears the CORS site (RRF Network::SetCorsSite with an empty string)");
    }

    /// <summary>
    /// M586.4 configures the MQTT client silently.
    /// </summary>
    /// <remarks>
    /// RRF Network::ConfigureNetworkProtocol dispatches command fraction 4 (MqttProtocol,
    /// NetworkDefs.h) to MqttClient::Configure, which accepts client id, username and password
    /// while the client is disabled and replies nothing. MQTT settings have no object model
    /// fields in RRF
    /// </remarks>
    [Test]
    public async Task M586Dot4ConfiguresTheMqttClient()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M586.4 C\"benchclient\" U\"user\" K\"pass\"");
        Assert.That(reply.Trim(), Is.Empty, "M586.4 configures the MQTT client without a report (RRF MqttClient::Configure)");
    }

    /// <summary>
    /// M929 starts event logging, reported as state.logFile and state.logLevel, and S0 stops it
    /// again.
    /// </summary>
    /// <remarks>
    /// RRF Platform::ConfigureLogging (Platform.cpp): S1..S3 map to warn, info and debug; the
    /// object model reports state.logLevel (off while inactive) and state.logFile, null when
    /// logging is not active (RepRap.cpp state table, Platform::GetLogFileName)
    /// </remarks>
    [Test]
    public async Task M929StartsAndStopsEventLogging()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M929 S3 P\"eventlog.txt\"");
        Assert.That(reply, Does.Not.Contain("Error"), "M929 S3 starts logging (RRF Platform::ConfigureLogging)");
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.Model.State.LogLevel, Is.EqualTo(EventLogLevel.Debug),
                            "M929 S3 sets state.logLevel to debug (RRF Platform::ConfigureLogging; OM RepRap.cpp logLevel)");
                Assert.That(bench.Host.Model.State.LogFile, Is.EqualTo("eventlog.txt"),
                            "M929 P names state.logFile (RRF Platform::GetLogFileName; OM RepRap.cpp logFile)");
            });
        }

        await bench.Host.ExecuteCodeAsync("M929 S0");
        using (await bench.Host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.Model.State.LogLevel, Is.EqualTo(EventLogLevel.Off),
                            "M929 S0 sets state.logLevel to off (RRF Platform::GetLogLevel while inactive)");
                Assert.That(bench.Host.Model.State.LogFile, Is.Null,
                            "M929 S0 clears state.logFile (RRF Platform::GetLogFileName returns null while inactive)");
            });
        }
    }

    /// <summary>
    /// M952 sends new CAN timing to an expansion board over the bus and reports nothing.
    /// </summary>
    /// <remarks>
    /// RRF CanInterface::ChangeAddressAndNormalTiming sends CanMessageSetAddressAndNormalTiming to
    /// the board named by B and replies ok. There is no object model field for the bus timing, so
    /// the observable is the CAN message leaving for the board
    /// </remarks>
    [Test]
    public async Task M952ConfiguresExpansionBoardCanTiming()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int sendsBefore = bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count;

        string reply = await bench.Host.ExecuteCodeAsync("M952 B1 S500");
        Assert.That(reply, Does.Not.Contain("Error"), "M952 B1 S500 accepts the new timing (RRF CanInterface::ChangeAddressAndNormalTiming)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage).Count > sendsBefore,
                                             what: "M952's timing message leaving for board 1");
    }

    /// <summary>
    /// M953 enables the CAN bus: a second enable reaches the controller and the code reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// RRF CanInterface::EnableCan enables the bus with the default data rate when no parameter
    /// changes the timing. There is no object model field for the bus state, so the observable is
    /// the enable crossing the link (config.g already sent the first one)
    /// </remarks>
    [Test]
    public async Task M953EnablesTheCanBus()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int enablesBefore = bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count;

        string reply = await bench.Host.ExecuteCodeAsync("M953");
        Assert.That(reply, Does.Not.Contain("Error"), "M953 enables CAN without a report (RRF CanInterface::EnableCan)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count > enablesBefore,
                                             what: "M953's enable reaching the controller");
    }

    /// <summary>
    /// M957 raises an event whose macro runs, observed through a global the macro sets.
    /// </summary>
    /// <remarks>
    /// RRF GCodes::RaiseEvent (GCodes3.cpp) queues the event; GCodes::ProcessEvent runs the macro
    /// named by Event::GetMacroFileName, driver-warning.g for driver_warning (Event.cpp,
    /// underscores become dashes). driver_warning's default action only logs, so the raised event
    /// leaves the machine running
    /// </remarks>
    [Test]
    public async Task M957RaisesAnEventAndRunsItsMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global eventRan = 0\n",
            prepareSd: sd => sd.WriteSys("driver-warning.g", "set global.eventRan = global.eventRan + 1\n"));

        string reply = await bench.Host.ExecuteCodeAsync("M957 E\"driver_warning\" D0 B1");
        Assert.That(reply, Does.Not.Contain("Error"), "M957 accepts a driver_warning event (RRF GCodes::RaiseEvent)");
        await WaitForGlobalAsync(bench.Host, "eventRan", 1);
        Assert.That(await bench.Host.GlobalAsync("eventRan"), Is.EqualTo(1),
                    "the raised event ran driver-warning.g once (RRF GCodes::ProcessEvent, Event::GetMacroFileName)");
    }
}
