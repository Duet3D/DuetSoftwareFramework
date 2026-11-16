using System.Runtime.CompilerServices;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Files;

/// <summary>
/// What M500 needs to know about config-override.g that the object model does not say
/// </summary>
/// <remarks>
/// Neither is configuration a board needs, and RepRapFirmware does not report either in its object
/// model: they are about where a setting came from and whether it will be loaded again
/// </remarks>
internal sealed class ConfigOverrideTracker
{
    /// <summary>
    /// Whether config.g, or a macro it called, ran M501
    /// </summary>
    /// <remarks>RepRapFirmware's <c>m501SeenInConfigFile</c>; M500 warns while this is false</remarks>
    public bool LoadedByConfig { get; private set; }

    /// <summary>Probes whose G31 values M500 writes without being asked to by P31</summary>
    /// <remarks>Keyed by the probe itself, so a probe that is replaced starts unmarked</remarks>
    private readonly ConditionalWeakTable<Probe, object> _savedProbes = [];

    /// <summary>Note that config.g loaded config-override.g</summary>
    public void NoteLoadedByConfig() => LoadedByConfig = true;

    /// <summary>
    /// Mark a probe's values as ones M500 writes from now on
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <remarks>RepRapFirmware's <c>ZProbe::SetSaveToConfigOverride</c>, which nothing clears</remarks>
    public void SaveProbe(Probe probe) => _savedProbes.AddOrUpdate(probe, probe);

    /// <summary>
    /// Whether M500 writes a probe's values
    /// </summary>
    /// <param name="probe">The probe</param>
    /// <returns>True once P31 or config-override.g has set it</returns>
    public bool IsProbeSaved(Probe probe) => _savedProbes.TryGetValue(probe, out _);
}
