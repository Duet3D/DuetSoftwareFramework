using System.IO;
using System.Linq;
using DuetEmulation;
using NUnit.Framework;

namespace EmulationTests;

/// <summary>Where each fixture's emulated system keeps its logs, traces and images</summary>
internal static class Runs
{
    /// <summary>
    /// A directory for a fixture's run, under build/emulation/runs, named after the fixture and its
    /// parameters (the stack it runs on)
    /// </summary>
    public static string Directory(object fixture)
    {
        string name = TestContext.CurrentContext.Test.FullName ?? fixture.GetType().FullName!;
        char[] invalid = [.. Path.GetInvalidFileNameChars(), '(', ')', '"', ' ', ','];
        string safe = new([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
        return Path.Combine(RepositoryPaths.Emulation, "runs", safe.Trim('_'));
    }
}
