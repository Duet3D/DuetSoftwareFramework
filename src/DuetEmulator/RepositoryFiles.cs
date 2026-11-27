using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuetEmulation;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;

namespace DuetEmulator;

/// <summary>A scenario file in the repository</summary>
/// <param name="Path">Its path from the repository's root, with forward slashes</param>
/// <param name="Scenario">What it holds, or null if it does not read</param>
/// <param name="Error">Why it does not read, or null</param>
internal sealed record ScenarioFile(string Path, Scenario? Scenario, string? Error);

/// <summary>A saved run: a scenario run's or a session's captures, one per stack</summary>
/// <param name="Id">How the API names it: its kind and name, such as <c>scenarios/endstop-homing</c></param>
/// <param name="Name">Its directory's name</param>
/// <param name="Stacks">The stacks it has a capture of</param>
/// <param name="Modified">When its newest capture was written</param>
internal sealed record SavedRun(string Id, string Name, IReadOnlyList<string> Stacks, DateTime Modified);

/// <summary>The scenario files and saved runs in the repository, and where a scenario may be saved</summary>
internal static class RepositoryFiles
{
    /// <summary>Where the scenarios kept with the bench are</summary>
    public static string ScenariosDirectory => Path.Combine(RepositoryPaths.Root, "src", "DuetEmulation", "Scenarios");

    private static readonly string[] RunKinds = ["scenarios", "sessions"];
    private static readonly string[] Stacks = ["rrf", "dsf"];

    /// <summary>Every scenario file under <see cref="ScenariosDirectory"/></summary>
    public static IReadOnlyList<ScenarioFile> Scenarios() =>
        [.. Directory.EnumerateFiles(ScenariosDirectory, "*.*", SearchOption.AllDirectories)
            .Where(IsScenarioFile)
            .Order(StringComparer.Ordinal)
            .Select(Read)];

    /// <summary>A scenario file, by its path from the repository's root</summary>
    /// <exception cref="ArgumentException">The path is outside the repository or not a scenario file</exception>
    /// <exception cref="FileNotFoundException">There is no such file</exception>
    public static ScenarioFile Scenario(string path)
    {
        string full = ScenarioPath(path);
        return File.Exists(full) ? Read(full) : throw new FileNotFoundException($"There is no scenario {path}", path);
    }

    /// <summary>
    /// The full path of a scenario file given by its path from the repository's root, which must stay
    /// in the repository and name a YAML file
    /// </summary>
    /// <exception cref="ArgumentException">It does not</exception>
    public static string ScenarioPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new ArgumentException($"\"{path}\" is not a path from the repository's root");
        }
        string root = Path.GetFullPath(RepositoryPaths.Root);
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{path} is outside the repository");
        }
        if (!IsScenarioFile(full))
        {
            throw new ArgumentException($"{path} is not a YAML file (.yaml or .yml)");
        }
        return full;
    }

    /// <summary>The path of a file from the repository's root, with forward slashes</summary>
    public static string RelativePath(string full) => Path.GetRelativePath(RepositoryPaths.Root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsScenarioFile(string path) =>
        Path.GetExtension(path) is ".yaml" or ".yml";

    private static ScenarioFile Read(string full)
    {
        string path = RelativePath(full);
        try
        {
            return new ScenarioFile(path, DuetEmulation.Scenarios.Scenario.Load(full), null);
        }
        catch (FormatException e)
        {
            return new ScenarioFile(path, null, e.Message);
        }
    }

    /// <summary>The saved runs under build/emulation, newest first</summary>
    public static IReadOnlyList<SavedRun> Runs()
    {
        List<SavedRun> runs = [];
        foreach (string kind in RunKinds)
        {
            string directory = Path.Combine(RepositoryPaths.Emulation, kind);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            foreach (string run in Directory.EnumerateDirectories(directory))
            {
                List<string> captures = [.. Stacks.Select(s => Path.Combine(run, s, MachineCapture.FileName)).Where(File.Exists)];
                if (captures.Count > 0)
                {
                    runs.Add(new SavedRun($"{kind}/{Path.GetFileName(run)}", Path.GetFileName(run),
                                          [.. captures.Select(c => Path.GetFileName(Path.GetDirectoryName(c))!)], captures.Max(File.GetLastWriteTimeUtc)));
                }
            }
        }
        return [.. runs.OrderByDescending(r => r.Modified)];
    }

    /// <summary>A saved run's captures by stack name</summary>
    /// <exception cref="ArgumentException">The id does not name a run</exception>
    /// <exception cref="FileNotFoundException">There is no such run</exception>
    public static IReadOnlyDictionary<string, MachineCapture> Run(string id)
    {
        string[] parts = id.Split('/');
        if (parts.Length != 2 || !RunKinds.Contains(parts[0]) || parts[1].Length == 0 || parts[1] != Output.SafeName(parts[1]))
        {
            throw new ArgumentException($"\"{id}\" is not a saved run, such as scenarios/endstop-homing");
        }
        string directory = Path.Combine(RepositoryPaths.Emulation, parts[0], parts[1]);
        Dictionary<string, MachineCapture> captures = Stacks
            .Where(s => File.Exists(Path.Combine(directory, s, MachineCapture.FileName)))
            .ToDictionary(s => s, s => MachineCapture.Load(Path.Combine(directory, s)));
        return captures.Count > 0 ? captures : throw new FileNotFoundException($"There is no saved run {id}");
    }
}
