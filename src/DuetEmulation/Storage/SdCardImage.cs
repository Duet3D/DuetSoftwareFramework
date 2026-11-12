using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DuetEmulation.Storage;

/// <summary>
/// An SD card image: a FAT32 filesystem holding the files a main board finds on its card
/// (sys/config.g and so on), made with mkfs.vfat and mtools
/// </summary>
public static class SdCardImage
{
    /// <summary>The image size. FAT32 needs at least 65525 clusters, which at one 512-byte sector each is 32MiB</summary>
    public const long Size = 64L * 1024 * 1024;

    /// <summary>Write an image holding <paramref name="files"/>, which maps paths on the card ("sys/config.g") to their contents</summary>
    /// <exception cref="InvalidOperationException">A tool failed or is not installed</exception>
    public static void Create(string imagePath, IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        using (FileStream image = new(imagePath, FileMode.Create, FileAccess.Write))
        {
            image.SetLength(Size);
        }
        Run("mkfs.vfat", "-F", "32", "-n", "DUET", imagePath);

        HashSet<string> made = [];
        foreach ((string path, byte[] contents) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            string[] parts = path.Trim('/').Split('/');
            for (int depth = 1; depth < parts.Length; depth++)
            {
                string directory = string.Join('/', parts[..depth]);
                if (made.Add(directory))
                {
                    Run("mmd", "-i", imagePath, "::/" + directory);
                }
            }

            string staged = Path.Combine(Path.GetDirectoryName(imagePath)!, $".sd-{Guid.NewGuid():N}");
            File.WriteAllBytes(staged, contents);
            try
            {
                Run("mcopy", "-i", imagePath, staged, "::/" + string.Join('/', parts));
            }
            finally
            {
                File.Delete(staged);
            }
        }
    }

    /// <summary>A text file's contents as the firmware reads them, with CRLF line endings as DWC writes</summary>
    public static byte[] Text(string text) =>
        Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    private static void Run(string tool, params string[] arguments)
    {
        ProcessStartInfo startInfo = new(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        // mtools refuses an image whose geometry it does not recognise
        startInfo.Environment["MTOOLS_SKIP_CHECK"] = "1";

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"{tool} is needed to build SD card images: install dosfstools and mtools", e);
        }
        using (process)
        {
            string error = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{tool} {string.Join(' ', arguments)} failed: {error.Trim()}");
            }
        }
    }
}
