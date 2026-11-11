using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Drivers;

/// <summary>M914, which DSF does not support</summary>
[TestFixture]
[GCode("M914")]
public class M914Tests : BenchFixture
{
    /// <summary>
    /// M914 is not a command this firmware has, and is declined as a warning
    /// </summary>
    /// <remarks>
    /// <c>testcases/drivers/m914-voltage-level-translator.yaml</c>. It sets an expansion header's
    /// signal level on the Alligator board, which is a level translator rather than a CAN device, so
    /// no rig can have one. A warning rather than an error is RepRapFirmware's
    /// <c>warningNotSupported</c>, which is what an M-code with no handler at all falls through to
    /// </remarks>
    [Test]
    public async Task M914IsNotSupported()
    {
        await using Bench bench = await Bench.StartAsync(DriversRig.Config);

        Assert.That((await bench.Host.ExecuteCodeAsync("M914")).TrimEnd(),
                    Is.EqualTo("Warning: M914: Command is not supported"));
    }
}
