using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using bottlenoselabs.C2CS.Runtime;
using static Tracy.PInvoke;

namespace DuetControlServer.Profiling;

/// <summary>
/// Tracy client the woven zones are reported to
/// </summary>
/// <remarks>
/// Only compiled into a build made with -p:Profiling=true, and called only from the calls the Fody
/// weaver in src/DuetProfiling.Fody puts into the profiled methods. The client library it talks to
/// listens for the Tracy GUI on TCP port 8086 and, built on demand by scripts/build-tracy-client.sh,
/// records nothing until the GUI connects.
/// </remarks>
internal static unsafe class TracyProfiler
{
    /// <summary>
    /// Locate the Tracy client library before the first call into it
    /// </summary>
    /// <remarks>
    /// A published DuetControlServer has the library beside its assemblies where default P/Invoke
    /// probing finds it, but a test host run from the build tree does not, so the library is looked
    /// up explicitly. The static constructor runs before <see cref="RegisterZone"/> makes the first
    /// call, which is the only ordering requirement a resolver has.
    /// </remarks>
    static TracyProfiler()
    {
        NativeLibrary.SetDllImportResolver(typeof(TracyCZoneCtx).Assembly, static (name, _, _) =>
            name == TracyClientLibrary ? NativeLibrary.Load(LocateClientLibrary()) : IntPtr.Zero);
    }

    /// <summary>
    /// Name the managed binding imports the Tracy client under
    /// </summary>
    private const string TracyClientLibrary = "TracyClient";

    /// <summary>
    /// Environment variable naming a specific client library to profile against
    /// </summary>
    private const string LibraryPathEnvironmentVariable = "TRACY_CLIENT_LIBRARY";

    /// <summary>
    /// Whether the calling thread has told Tracy its name yet
    /// </summary>
    [ThreadStatic]
    private static bool _threadNamed;

    /// <summary>
    /// Describe a profiled method to Tracy
    /// </summary>
    /// <param name="name">Name to show on the zone itself</param>
    /// <param name="function">Name to show in the zone info and statistics panes</param>
    /// <returns>Source location to open zones for this method with</returns>
    /// <remarks>
    /// Called once per profiled method, the first time it runs. Tracy keeps the pointer it is
    /// handed and reads through it whenever the trace is written out, so the location and the
    /// strings in it are allocated unmanaged and never freed.
    /// </remarks>
    internal static IntPtr RegisterZone(string name, string function)
    {
        TracySourceLocationData* location = (TracySourceLocationData*)NativeMemory.AllocZeroed((nuint)sizeof(TracySourceLocationData));

        // The properties allocate an unmanaged copy of each string, which is what Tracy needs
        location->Name = name;
        location->Function = function;

        // Tracy pairs a file and a line to offer the source of a zone. Weaving happens after
        // compilation and the weaver works from the method rather than the source, so neither is
        // known here
        location->File = string.Empty;
        location->Line = 0;
        location->Color = 0;
        return (IntPtr)location;
    }

    /// <summary>
    /// Open a zone for a method being entered
    /// </summary>
    /// <param name="location">Source location from <see cref="RegisterZone"/></param>
    /// <returns>Zone to pass back to <see cref="EndZone"/></returns>
    internal static TracyCZoneCtx BeginZone(IntPtr location)
    {
        NameThread();
        return TracyEmitZoneBegin((TracySourceLocationData*)location, 1);
    }

    /// <summary>
    /// Tell Tracy what the calling thread is called, the first time that thread reports anything
    /// </summary>
    /// <remarks>
    /// Without a name the timeline labels a thread by its OS id, and .NET thread pool threads are
    /// unnamed, so this is what tells the rows apart from each other. Tracy copies the name, so the
    /// unmanaged string does not have to outlive this call.
    /// </remarks>
    private static void NameThread()
    {
        if (!_threadNamed)
        {
            _threadNamed = true;
            using CString threadName = CString.FromString(Thread.CurrentThread.Name ?? $"Thread {Environment.CurrentManagedThreadId}");
            TracySetThreadName(threadName);
        }
    }

    /// <summary>
    /// Whether a Tracy GUI is capturing
    /// </summary>
    /// <remarks>
    /// The client is built on demand, so it discards everything reported to it until a GUI connects.
    /// Zones are cheap enough to emit regardless; this is for callers that would have to do work of
    /// their own to produce what they report.
    /// </remarks>
    internal static bool Connected => TracyConnected() != 0;

    /// <summary>
    /// Report a message to Tracy
    /// </summary>
    /// <param name="text">Message to show</param>
    /// <param name="colour">Colour to show it in, as 0xRRGGBB</param>
    /// <remarks>
    /// Tracy attributes the message to the thread that reported it and marks it on that thread's
    /// timeline, so it lands among the zones that were open at the time. The client copies the text,
    /// which is why the unmanaged string does not have to outlive this call.
    /// </remarks>
    internal static void Message(string text, uint colour)
    {
        NameThread();
        using CString message = CString.FromString(text);
        TracyEmitMessageC(message, (ulong)Encoding.UTF8.GetByteCount(text), colour, 0);
    }

    /// <summary>
    /// Close the zone opened for a method being left
    /// </summary>
    /// <param name="zone">Zone returned by <see cref="BeginZone"/></param>
    /// <remarks>
    /// Called from the finally block the weaver puts around the method, so a method that throws
    /// still closes its zone.
    /// </remarks>
    internal static void EndZone(TracyCZoneCtx zone) => TracyEmitZoneEnd(zone);

    /// <summary>
    /// Find the Tracy client library to load
    /// </summary>
    /// <returns>Path to the client library</returns>
    /// <exception cref="DllNotFoundException">Client library has not been built</exception>
    private static string LocateClientLibrary()
    {
        string? overridePath = Environment.GetEnvironmentVariable(LibraryPathEnvironmentVariable);
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath)
                ? overridePath
                : throw new DllNotFoundException($"{LibraryPathEnvironmentVariable} points at '{overridePath}', which does not exist");
        }

        // Beside the assemblies in a deployment, in the build tree the script wrote it to otherwise
        string libraryName = $"{TracyClientLibrary}.so";
        string deployedPath = Path.Combine(AppContext.BaseDirectory, libraryName);
        if (File.Exists(deployedPath))
        {
            return deployedPath;
        }

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm",
            Architecture.X64 => "linux-x64",
            Architecture other => throw new DllNotFoundException($"No Tracy client is built for {other}")
        };

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string buildTreePath = Path.Combine(directory.FullName, "build", "tracy", architecture, libraryName);
            if (File.Exists(buildTreePath))
            {
                return buildTreePath;
            }
            directory = directory.Parent;
        }

        throw new DllNotFoundException(
            $"The Tracy client has not been built for {architecture}. Build it with:\n" +
            $"  scripts/build-tracy-client.sh --arch {architecture}\n" +
            $"or point {LibraryPathEnvironmentVariable} at a client library to use instead");
    }
}
