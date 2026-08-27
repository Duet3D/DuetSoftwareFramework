using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace SystemTests;

/// <summary>
/// Resolves <c>libduet_sbc.so</c> for the whole test assembly. The tests run the real native
/// library, which lives in the CMake build tree rather than beside the managed assemblies, so this
/// registers a resolver on the DuetControlServer assembly that probes the tree - preferring the
/// freshest build so a rebuilt library is picked up without a copy step.
/// </summary>
/// <remarks>
/// Nothing references this class: NUnit finds it through the <c>[SetUpFixture]</c> attribute and
/// runs <see cref="RegisterResolver"/> once before any test in the assembly, which is the only
/// point early enough to install a P/Invoke resolver. Set the <c>DUET_SBC_LIBRARY</c> environment
/// variable to test against a specific build.
/// </remarks>
[SetUpFixture]
public sealed class NativeLibraryLocator
{
    [OneTimeSetUp]
    public void RegisterResolver()
    {
        string libraryPath = Locate();
        NativeLibrary.SetDllImportResolver(typeof(DuetControlServer.Settings).Assembly,
            (name, _, _) => name == "duet_sbc" ? NativeLibrary.Load(libraryPath) : IntPtr.Zero);
    }

    private static string Locate()
    {
        string? overridePath = Environment.GetEnvironmentVariable("DUET_SBC_LIBRARY");
        if (overridePath != null)
        {
            return File.Exists(overridePath)
                ? overridePath
                : throw new FileNotFoundException($"DUET_SBC_LIBRARY points at '{overridePath}', which does not exist");
        }

        // Walk up from the test assembly to the repository root
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DuetSbcInterface")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Cannot find the repository root above " + AppContext.BaseDirectory);
        }

        string buildTree = Path.Combine(dir.FullName, "src", "DuetSbcInterface", "build");
        string[] candidates =
        [
            Path.Combine(buildTree, "native-debug", "src", "libduet_sbc.so"),
            Path.Combine(buildTree, "native", "src", "libduet_sbc.so"),
        ];
        string? newest = candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return newest ?? throw new InvalidOperationException(
            "libduet_sbc.so has not been built for the host. Build it with:\n" +
            "  cd src/DuetSbcInterface && cmake --preset native-debug && cmake --build --preset native-debug --target duet_sbc_shared");
    }
}
