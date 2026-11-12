using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DuetRealtimeCore;

/// <summary>
/// Resolves <c>libduet_realtime_core.so</c> built for the development host, for code that runs
/// DuetControlServer in-process. The library lives in the CMake build tree rather than beside the
/// managed assemblies, so this probes the tree, preferring the freshest build so that a rebuilt library
/// is picked up without a copy step. Set <c>DUET_REALTIME_CORE_LIBRARY</c> to use a specific build.
/// </summary>
/// <remarks>Compiled into each project that imports HostLibrary.targets</remarks>
internal static class HostLibraryLocator
{
    private const string LibraryName = "duet_realtime_core";

    /// <summary>Resolve the library for P/Invokes declared in the given assemblies</summary>
    /// <remarks>A resolver can be set once per assembly, so call this once, before the first P/Invoke</remarks>
    public static void Register(params Assembly[] assemblies)
    {
        string libraryPath = Locate();
        DllImportResolver resolver = (name, _, _) => name == LibraryName ? NativeLibrary.Load(libraryPath) : IntPtr.Zero;
        foreach (Assembly assembly in assemblies)
        {
            NativeLibrary.SetDllImportResolver(assembly, resolver);
        }
    }

    /// <summary>The path of the library to load</summary>
    public static string Locate()
    {
        string? overridePath = Environment.GetEnvironmentVariable("DUET_REALTIME_CORE_LIBRARY");
        if (overridePath != null)
        {
            return File.Exists(overridePath)
                ? overridePath
                : throw new FileNotFoundException($"DUET_REALTIME_CORE_LIBRARY points at '{overridePath}', which does not exist");
        }

        // Walk up from the running assembly to the repository root
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DuetRealtimeCore")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Cannot find the repository root above " + AppContext.BaseDirectory);
        }

        string buildTree = Path.Combine(dir.FullName, "build");
        string[] candidates =
        [
            Path.Combine(buildTree, "DuetRealtimeCore-native-debug", "libduet_realtime_core.so"),
            Path.Combine(buildTree, "DuetRealtimeCore-native", "libduet_realtime_core.so"),
        ];
        string? newest = candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return newest ?? throw new InvalidOperationException(
            "libduet_realtime_core.so has not been built for the host. Build it with:\n" +
            "  cmake --preset DuetRealtimeCore-native-debug && cmake --build --preset DuetRealtimeCore-native-debug --target duet_realtime_core_shared");
    }
}
