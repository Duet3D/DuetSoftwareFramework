using Microsoft.Extensions.FileProviders;
using System;
using System.IO;

namespace DuetWebServer.FileProviders;

/// <summary>
/// IFileInfo implementation for symlinked files that resolves the link target to get correct
/// file metadata. On Linux, FileInfo.Length for a symlink returns the length of the target
/// path string (via lstat) rather than the actual file content size, causing Content-Length
/// to be set incorrectly and truncated responses. This class uses the resolved target
/// </summary>
/// <param name="target">Resolved final symlink target</param>
/// <param name="name">Name of the symlink entry as seen in the web root</param>
internal sealed class SymlinkFileInfo(FileInfo target, string name) : IFileInfo
{
    /// <inheritdoc/>
    public bool Exists => target.Exists;

    /// <inheritdoc/>
    public long Length => target.Length;

    /// <inheritdoc/>
    public string? PhysicalPath => target.FullName;

    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public DateTimeOffset LastModified => new(target.LastWriteTimeUtc, TimeSpan.Zero);

    /// <inheritdoc/>
    public bool IsDirectory => false;

    /// <inheritdoc/>
    public Stream CreateReadStream() =>
        new FileStream(target.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
}
