using System.IO;

namespace SystemTests.Host;

/// <summary>
/// The per-test virtual SD card: the directory tree DuetControlServer treats as <c>0:/</c>.
/// Populate it before the host starts; <c>config.g</c> written here is what the startup runs
/// </summary>
internal sealed class VirtualSd
{
    /// <summary>Physical root of the virtual SD card</summary>
    public string Root { get; }

    public VirtualSd(string root)
    {
        Root = root;
        foreach (string subdir in new[] { "sys", "gcodes", "macros" })
        {
            Directory.CreateDirectory(Path.Combine(root, subdir));
        }
    }

    /// <summary>Write a system file (0:/sys), e.g. config.g or the pause/resume macros</summary>
    public void WriteSys(string name, string content)
        => File.WriteAllText(Path.Combine(Root, "sys", name), content);

    /// <summary>Write a job file (0:/gcodes)</summary>
    public void WriteGCode(string name, string content)
        => File.WriteAllText(Path.Combine(Root, "gcodes", name), content);
}
