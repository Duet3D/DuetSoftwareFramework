using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuetEmulation.Boards;

/// <summary>
/// The symbols of a 32-bit little-endian ELF image, by mangled name. Renode's own symbol lookup knows
/// only code, and the bench reads firmware variables
/// </summary>
public sealed class ElfSymbols
{
    private const int SectionHeaderOffsetField = 0x20;
    private const int SectionHeaderSizeField = 0x2E;
    private const int SectionCountField = 0x30;
    private const uint SymbolTableType = 2;
    private const int SymbolSize = 16;

    private static readonly ConcurrentDictionary<string, ElfSymbols> Cache = new();

    private readonly Dictionary<string, uint> _symbols;

    private ElfSymbols(Dictionary<string, uint> symbols) => _symbols = symbols;

    /// <summary>The symbols of an image, read once per path and modification time</summary>
    public static ElfSymbols Load(string path) =>
        Cache.GetOrAdd($"{Path.GetFullPath(path)}|{File.GetLastWriteTimeUtc(path).Ticks}", _ => new ElfSymbols(Read(File.ReadAllBytes(path))));

    /// <summary>The address of a symbol</summary>
    /// <exception cref="KeyNotFoundException">The image has no such symbol</exception>
    public uint this[string name] =>
        _symbols.TryGetValue(name, out uint address) ? address : throw new KeyNotFoundException($"The image has no symbol {name}");

    /// <summary>The address of a symbol, if the image has it</summary>
    public bool TryGet(string name, out uint address) => _symbols.TryGetValue(name, out address);

    private static Dictionary<string, uint> Read(byte[] image)
    {
        if (image.Length < 0x34 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
        {
            throw new InvalidDataException("Not an ELF image");
        }
        if (image[4] != 1 || image[5] != 1)
        {
            throw new InvalidDataException("Not a 32-bit little-endian ELF image");
        }

        ReadOnlySpan<byte> data = image;
        uint sectionHeaders = BinaryPrimitives.ReadUInt32LittleEndian(data[SectionHeaderOffsetField..]);
        int sectionHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data[SectionHeaderSizeField..]);
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data[SectionCountField..]);

        Dictionary<string, uint> symbols = [];
        for (int i = 0; i < sectionCount; i++)
        {
            ReadOnlySpan<byte> section = data.Slice((int)sectionHeaders + i * sectionHeaderSize, sectionHeaderSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(section[4..]) != SymbolTableType)
            {
                continue;
            }
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            int link = (int)BinaryPrimitives.ReadUInt32LittleEndian(section[24..]);
            ReadOnlySpan<byte> stringSection = data.Slice((int)sectionHeaders + link * sectionHeaderSize, sectionHeaderSize);
            int stringsOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(stringSection[16..]);
            int stringsSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(stringSection[20..]);
            ReadOnlySpan<byte> strings = data.Slice(stringsOffset, stringsSize);

            for (int entry = offset; entry + SymbolSize <= offset + size; entry += SymbolSize)
            {
                int nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[entry..]);
                if (nameOffset == 0 || nameOffset >= strings.Length)
                {
                    continue;
                }
                ReadOnlySpan<byte> name = strings[nameOffset..];
                int end = name.IndexOf((byte)0);
                symbols.TryAdd(Encoding.ASCII.GetString(end < 0 ? name : name[..end]),
                               BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 4)..]));
            }
        }
        return symbols;
    }
}
