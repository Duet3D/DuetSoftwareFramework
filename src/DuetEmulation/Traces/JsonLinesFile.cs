using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuetEmulation.Traces;

/// <summary>
/// A file of JSON lines that a Renode model appends to, read as it grows: each read returns the
/// complete lines written since the last
/// </summary>
internal sealed class JsonLinesFile(string path)
{
    private long _offset;

    /// <summary>The file</summary>
    public string Path { get; } = path;

    /// <summary>The complete lines written since the last call</summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        byte[] data;
        using (FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length <= _offset)
            {
                return [];
            }
            stream.Seek(_offset, SeekOrigin.Begin);
            data = new byte[stream.Length - _offset];
            stream.ReadExactly(data);
        }

        // The model may be part way through a line; that one is read next time
        int complete = System.Array.LastIndexOf(data, (byte)'\n') + 1;
        _offset += complete;
        return Encoding.UTF8.GetString(data, 0, complete).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
    }
}
