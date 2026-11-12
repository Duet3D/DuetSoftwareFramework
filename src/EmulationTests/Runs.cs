using System.IO;
using DuetEmulation;
using NUnit.Framework;

namespace EmulationTests;

/// <summary>Where each fixture's emulated system keeps its logs, traces and images</summary>
internal static class Runs
{
    /// <summary>A directory for a fixture's run, under build/emulation/runs, named after the fixture</summary>
    public static string Directory(object fixture) =>
        Path.Combine(RepositoryPaths.Emulation, "runs", TestContext.CurrentContext.Test.ClassName ?? fixture.GetType().FullName!);
}
