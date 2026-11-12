using DuetRealtimeCore;
using NUnit.Framework;

namespace SystemTests;

/// <summary>
/// Resolves <c>libduet_realtime_core.so</c> for the whole test assembly: the tests run the real native
/// library, built for the host (see HostLibraryLocator)
/// </summary>
/// <remarks>
/// Nothing references this class: NUnit finds it through the <c>[SetUpFixture]</c> attribute and
/// runs <see cref="RegisterResolver"/> once before any test in the assembly, which is the only
/// point early enough to install a P/Invoke resolver
/// </remarks>
[SetUpFixture]
public sealed class NativeLibraryLocator
{
    [OneTimeSetUp]
    public void RegisterResolver() =>
        // Both assemblies: DuetControlServer for the engine it drives, and this one for the test
        // seams the bench calls directly - see NativeTestClock
        HostLibraryLocator.Register(typeof(DuetControlServer.Settings).Assembly, typeof(NativeLibraryLocator).Assembly);
}
