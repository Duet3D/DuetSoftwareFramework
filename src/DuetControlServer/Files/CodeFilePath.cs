namespace DuetControlServer.Files;

/// <summary>
/// Representation of a code file path
/// </summary>
/// <param name="virtualFile">Virtual file path in RRF format</param>
/// <param name="physicalFile">Physical file path in OS format</param>
public readonly struct CodeFilePath(string virtualFile, string physicalFile)
{
    /// <summary>
    /// Filename of the macro file
    /// </summary>
    public string Virtual { get; } = virtualFile;

    /// <summary>
    /// Physical path of the macro file
    /// </summary>
    public string Physical { get; } = physicalFile;
}
