using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M667, retired in favour of M669</summary>
[TestFixture]
[GCode("M667")]
public class M667Tests : BenchFixture
{
    /// <summary>
    /// M667 is retired: it answers with an error naming M669 and changes nothing, so the
    /// kinematics stays at RRF's cartesian boot default
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 667 for the reply; RepRap/Move construction gives a machine that has run
    /// no kinematics code cartesian kinematics, so move.kinematics.name reports "cartesian"
    /// (CoreKinematics::GetName)
    /// </remarks>
    [Test]
    public async Task M667RepliesRetiredInFavourOfM669()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M667 S1");
        Assert.That(reply, Does.Contain("M667 is no longer supported - use M669 instead"),
                    "M667 answers with RRF's retirement error (GCodes2.cpp case 667)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.Cartesian),
                    "a refused M667 leaves move.kinematics.name at RRF's cartesian boot default");
    }
}
