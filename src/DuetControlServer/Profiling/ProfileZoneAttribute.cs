using System;

namespace DuetControlServer.Profiling;

/// <summary>
/// Names code that a profiling build surrounds with Tracy zones
/// </summary>
/// <param name="scope">Namespace or type to profile, covering everything below it</param>
/// <remarks>
/// Read by the Fody weaver in src/DuetProfiling.Fody once this assembly has been compiled, and
/// applied in ProfiledCode.cs. It carries no behaviour of its own: profiling a method is entirely a
/// matter of what the weaver does to the compiled assembly, which is why the code being profiled
/// needs no annotations and why nothing outside this folder knows the profiler exists.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class ProfileZoneAttribute(string scope) : Attribute
{
    /// <summary>
    /// Namespace or type to profile
    /// </summary>
    public string Scope { get; } = scope;
}
