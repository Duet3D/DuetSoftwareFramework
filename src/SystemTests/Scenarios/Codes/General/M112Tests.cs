using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M112, the emergency stop</summary>
[TestFixture]
[GCode("M112")]
public class M112Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync();
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
}
