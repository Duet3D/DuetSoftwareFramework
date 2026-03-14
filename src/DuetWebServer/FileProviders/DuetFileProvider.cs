using DuetWebServer.Singletons;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System.IO;

namespace DuetWebServer.FileProviders;

/// <summary>
/// File provider that uses DCS to resolve file paths
/// </summary>
public class DuetFileProvider : IFileProvider
{
    /// <summary>
    /// Physical file provider
    /// </summary>
    private PhysicalFileProvider _provider;

    /// <summary>
    /// Object model provider
    /// </summary>
    private readonly IModelProvider _modelProvider;

    /// <summary>
    /// Creates a new file resolver instance
    /// </summary>
    public DuetFileProvider(IModelProvider modelProvider)
    {
        _modelProvider = modelProvider;
        _modelProvider.OnWebDirectoryChanged += SetWebDirectory;
        _provider = new PhysicalFileProvider(_modelProvider.WebDirectory!);
    }

    /// <summary>
    /// Finalizer of this instance
    /// </summary>
    ~DuetFileProvider() => _modelProvider.OnWebDirectoryChanged -= SetWebDirectory;

    /// <summary>
    /// Gets the file info of the specified path
    /// </summary>
    /// <param name="subpath">Target path</param>
    /// <returns>File info</returns>
    public IFileInfo GetFileInfo(string subpath)
    {
        lock (this)
        {
            var fileInfo = _provider.GetFileInfo(subpath);

            // On Linux, FileInfo.Length for a symlink returns the length of the target path
            // string (via lstat) rather than the actual file content size. Resolve the symlink
            // to the final target so the correct Content-Length is reported to clients.
            if (fileInfo.Exists && fileInfo.PhysicalPath is not null)
            {
                try
                {
                    var fi = new FileInfo(fileInfo.PhysicalPath);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 &&
                        fi.ResolveLinkTarget(returnFinalTarget: true) is FileInfo resolvedTarget &&
                        resolvedTarget.Exists)
                    {
                        return new SymlinkFileInfo(resolvedTarget, fileInfo.Name);
                    }
                }
                catch
                {
                    // Ignore errors and fall through to return the original file info
                }
            }

            return fileInfo;
        }
    }

    /// <summary>
    /// Returns the contents of the given directory
    /// </summary>
    /// <param name="subpath">Target path</param>
    /// <returns>Directory contents</returns>
    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        lock (this)
        {
            return _provider.GetDirectoryContents(subpath);
        }
    }

    /// <summary>
    /// Creates a token that watches for changes
    /// </summary>
    /// <param name="filter">Watch filter</param>
    /// <returns>Change token</returns>
    public IChangeToken Watch(string filter)
    {
        lock (this)
        {
            return _provider.Watch(filter);
        }
    }

    /// <summary>
    /// Set the directory of the file provider
    /// </summary>
    /// <param name="webDirectory">New web directory</param>
    private void SetWebDirectory(string webDirectory)
    {
        lock (this)
        {
            _provider.Dispose();
            _provider = new PhysicalFileProvider(webDirectory);
        }
    }
}
