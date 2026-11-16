using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using NUnit.Framework;

namespace UnitTests.File;

/// <summary>
/// What M500 remembers about config-override.g between codes
/// </summary>
[TestFixture]
public class ConfigOverrideTrackerTests
{
    [Test]
    public void NothingIsSavedOrLoadedAtFirst()
    {
        ConfigOverrideTracker tracker = new();

        Assert.Multiple(() =>
        {
            Assert.That(tracker.LoadedByConfig, Is.False, "config.g has not run M501 yet");
            Assert.That(tracker.IsProbeSaved(new Probe()), Is.False, "no probe has been marked");
        });
    }

    [Test]
    public void LoadingByConfigSticks()
    {
        ConfigOverrideTracker tracker = new();
        tracker.NoteLoadedByConfig();

        Assert.That(tracker.LoadedByConfig, Is.True);
    }

    [Test]
    public void AProbeIsMarkedByIdentity()
    {
        // Two probes with the same values are two probes: one replaced by M558 starts unmarked
        ConfigOverrideTracker tracker = new();
        Probe saved = new(), other = new();
        tracker.SaveProbe(saved);
        tracker.SaveProbe(saved);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.IsProbeSaved(saved), Is.True, "marking twice is the same as once");
            Assert.That(tracker.IsProbeSaved(other), Is.False, "an equal probe is not the marked one");
        });
    }
}
