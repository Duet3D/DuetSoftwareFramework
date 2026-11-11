using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.CanBus;

/// <summary>M953, enabling the CAN bus and reporting its timing</summary>
[TestFixture]
[GCode("M953")]
public class M953Tests : BenchFixture
{
    /// <summary>
    /// A bare M953 asks the controller what the bus is running at, rather than answering from
    /// anything this side remembers
    /// </summary>
    /// <remarks>
    /// CanInterface::EnableCan's else branch reports when it saw no parameter, and reads the timing
    /// out of the CAN peripheral with can0dev-&gt;GetLocalCanTiming. The peripheral belongs to
    /// DuetCANMaster here, which answers the same query addressed to board 0: a
    /// setAddressAndNormalTiming with doSetTiming clear. Answering from a remembered value instead
    /// reported the defaults this side assumes rather than the timing the bus actually runs at, which
    /// on the bench is a different jump width. The regression case is
    /// <c>testcases/can/m953-report-can-timing.yaml</c>, and the reporting form is the only one of
    /// M953 safe to run on a bench: S, R, T, J and C change the arbitration rate on the main board
    /// alone, desynchronising it from every board on the bus with nothing able to change it back
    /// </remarks>
    [Test]
    public async Task M953AsksTheControllerForTheBusTiming()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M953");
        Assert.That(reply, Does.Not.StartWith("Error:"), "a bare M953 is a report, not an error");

        (byte board, CanMessageSetAddressAndNormalTiming query) =
            bench.CanMaster.LastCanMessage<CanMessageSetAddressAndNormalTiming>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(CanId.MasterAddress),
                        "the query goes to board 0, which is the controller driving the bus");
            Assert.That(query.OldAddress, Is.EqualTo(CanId.MasterAddress));
            Assert.That(query.DoSetTiming, Is.EqualTo(CanMessageSetAddressAndNormalTiming.DoSetTimingNo),
                        "and asks rather than tells, which is what makes it a report");
        });
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
        await using Bench bench = await Bench.StartAsync();
        int enablesBefore = bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count;

        string reply = await bench.Host.ExecuteCodeAsync("M953");
        Assert.That(reply, Does.Not.Contain("Error"), "M953 enables CAN without a report (RRF CanInterface::EnableCan)");
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.SbcPackets(SbcRequest.EnableCAN).Count > enablesBefore,
                                             what: "M953's enable reaching the controller");
    }
}
