using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M669, selecting the kinematics</summary>
[TestFixture]
[GCode("M669")]
public class M669Tests : BenchFixture
{
    /// <summary>
    /// M669 K also takes the geometry's name as a quoted string, which selects the same geometry as
    /// its number and clears the homed flags in the same way
    /// </summary>
    [Test]
    public async Task M669SelectsAGeometryByName()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 K\"corexy\"");
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        bool xHomed = await host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY));
            Assert.That(xHomed, Is.False, "a kinematics change clears the homed flags");
        });
    }

    /// <summary>
    /// A K that names no geometry, by number or by name, is an error and leaves the geometry as it was
    /// </summary>
    /// <remarks>RepRapFirmware's reply for a number, GCodes2.cpp case 669: "Unknown kinematics type %d"</remarks>
    [TestCase("M669 K12", "Error: M669: Unknown kinematics type 12")]
    [TestCase("M669 K\"cubic\"", "Error: M669: Unknown kinematics type \"cubic\"")]
    public async Task M669RefusesAKThatNamesNoGeometry(string code, string expected)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        string reply = await host.ExecuteCodeAsync(code);
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        Assert.Multiple(() =>
        {
            Assert.That(reply.TrimEnd(), Is.EqualTo(expected));
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY), "the geometry that was selected stays");
        });
    }

    /// <summary>
    /// M669 K1 selects CoreXY: the kinematics name changes, the inverse matrix carries the CoreXY
    /// motor coupling, the forward matrix is its inverse, and the homed flags are cleared
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 669 (SetKinematics then SetAllAxesNotHomed on a change) and the
    /// CoreKinematics constructor in Movement/Kinematics/CoreKinematics.cpp: for coreXY the
    /// inverse matrix rows are [1,1,0], [1,-1,0], [0,0,1] and Recalc inverts it into the forward
    /// matrix [0.5,0.5,0], [0.5,-0.5,0], [0,0,1]. OM fields (name, forwardMatrix, inverseMatrix)
    /// from the CoreKinematics object model tables in the same file
    /// </remarks>
    [Test]
    public async Task M669SelectsCoreXY()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        bool xHomed = await host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY), "M669 K1 sets move.kinematics.name (RRF CoreKinematics::GetName)");
            Assert.That(xHomed, Is.False, "a kinematics change clears the homed flags (RRF GCodes2.cpp case 669)");
        });

        // The matrices are read from the live model: their rows are plain arrays that the meta
        // expression evaluator cannot index into, and the values are what matters here
        float inverse00, inverse01, inverse10, inverse11, inverse22;
        float forward00, forward01, forward10, forward11, forward22;
        using (await host.Model.AccessReadOnlyAsync(System.Threading.CancellationToken.None))
        {
            DuetAPI.ObjectModel.CoreKinematics? core = host.Model.Move.Kinematics as DuetAPI.ObjectModel.CoreKinematics;
            Assert.That(core, Is.Not.Null, "M669 K1 publishes CoreKinematics under move.kinematics");
            inverse00 = core!.InverseMatrix[0][0];
            inverse01 = core.InverseMatrix[0][1];
            inverse10 = core.InverseMatrix[1][0];
            inverse11 = core.InverseMatrix[1][1];
            inverse22 = core.InverseMatrix[2][2];
            forward00 = core.ForwardMatrix[0][0];
            forward01 = core.ForwardMatrix[0][1];
            forward10 = core.ForwardMatrix[1][0];
            forward11 = core.ForwardMatrix[1][1];
            forward22 = core.ForwardMatrix[2][2];
        }
        Assert.Multiple(() =>
        {
            Assert.That(inverse00, Is.EqualTo(1).Within(1e-3), "coreXY move.kinematics.inverseMatrix[0][0] = 1");
            Assert.That(inverse01, Is.EqualTo(1).Within(1e-3), "coreXY move.kinematics.inverseMatrix[0][1] = 1");
            Assert.That(inverse10, Is.EqualTo(1).Within(1e-3), "coreXY move.kinematics.inverseMatrix[1][0] = 1");
            Assert.That(inverse11, Is.EqualTo(-1).Within(1e-3), "coreXY move.kinematics.inverseMatrix[1][1] = -1");
            Assert.That(inverse22, Is.EqualTo(1).Within(1e-3), "coreXY leaves Z uncoupled: inverseMatrix[2][2] = 1");
            Assert.That(forward00, Is.EqualTo(0.5).Within(1e-3), "the forward matrix is the inverse: forwardMatrix[0][0] = 0.5");
            Assert.That(forward01, Is.EqualTo(0.5).Within(1e-3), "forwardMatrix[0][1] = 0.5");
            Assert.That(forward10, Is.EqualTo(0.5).Within(1e-3), "forwardMatrix[1][0] = 0.5");
            Assert.That(forward11, Is.EqualTo(-0.5).Within(1e-3), "forwardMatrix[1][1] = -0.5");
            Assert.That(forward22, Is.EqualTo(1).Within(1e-3), "forwardMatrix[2][2] = 1");
        });

        double inverseRows = await host.ReadModelAsync(model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Count);
        double forwardRows = await host.ReadModelAsync(model => ((CoreKinematics)model.Move.Kinematics).ForwardMatrix.Count);
        Assert.Multiple(() =>
        {
            Assert.That(inverseRows, Is.EqualTo(3), "move.kinematics.inverseMatrix has one row per axis (RRF CoreKinematics array table, count reprap.GetGCodes().GetTotalAxes())");
            Assert.That(forwardRows, Is.EqualTo(3), "move.kinematics.forwardMatrix has one row per visible axis");
        });
    }
}
