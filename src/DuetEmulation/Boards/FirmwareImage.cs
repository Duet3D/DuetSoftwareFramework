using System.IO;

namespace DuetEmulation.Boards;

/// <summary>
/// A firmware build: the image the build ships (the .bin, with its CRC where the board checks one) and
/// the .elf it was made from
/// </summary>
/// <param name="Preset">The CMake preset that built it</param>
/// <param name="Binary">The shipped image</param>
/// <param name="Elf">The linked program, for its symbols</param>
public sealed record FirmwareImage(string Preset, string Binary, string Elf)
{
    /// <summary>The image a preset's build produced, in build/&lt;preset&gt;/&lt;preset&gt;.bin</summary>
    /// <exception cref="FileNotFoundException">The preset has not been built</exception>
    public static FirmwareImage Find(string preset)
    {
        string directory = Path.Combine(RepositoryPaths.Build, preset);
        string binary = Path.Combine(directory, preset + ".bin");
        string elf = Path.Combine(directory, preset + ".elf");
        if (!File.Exists(binary) || !File.Exists(elf))
        {
            throw new FileNotFoundException($"{preset} has not been built: run scripts/build.py {preset}", binary);
        }
        return new FirmwareImage(preset, binary, elf);
    }

    /// <summary>The image's symbols</summary>
    public ElfSymbols Symbols => ElfSymbols.Load(Elf);
}
