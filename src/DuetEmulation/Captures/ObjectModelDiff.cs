using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DuetEmulation.Captures;

/// <summary>One value of the object model that changed</summary>
/// <param name="Path">Where it is, as an M409 key (<c>boards[1].drivers[0].status</c>)</param>
/// <param name="Old">What it was, or null if it did not exist or was null</param>
/// <param name="New">What it became, or null if it no longer exists or is null</param>
public sealed record ObjectModelChange(string Path, JsonNode? Old, JsonNode? New)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Path}: {Format(Old)} -> {Format(New)}";

    private static string Format(JsonNode? node) => node?.ToJsonString() ?? "null";
}

/// <summary>
/// The changes between two snapshots of the object model, down to the values that changed: a member
/// or element that appeared or went is one change, and an array that changed length is compared element
/// by element as far as both reach
/// </summary>
public static class ObjectModelDiff
{
    /// <summary>Every value that differs between <paramref name="before"/> and <paramref name="after"/>, in document order</summary>
    /// <param name="before">The earlier snapshot</param>
    /// <param name="after">The later snapshot</param>
    /// <param name="ignore">Paths left out, with their descendants; an index may be <c>[*]</c> to match any</param>
    public static IReadOnlyList<ObjectModelChange> Compare(JsonNode? before, JsonNode? after, IEnumerable<string>? ignore = null)
    {
        List<ObjectModelChange> changes = [];
        PathFilter filter = new(ignore ?? []);
        Walk(string.Empty, before, after, changes, filter);
        return changes;
    }

    /// <summary>A copy of <paramref name="model"/> without the paths <paramref name="ignore"/> names</summary>
    public static JsonNode? Without(JsonNode? model, IEnumerable<string> ignore)
    {
        PathFilter filter = new(ignore);
        return Prune(string.Empty, model, filter);
    }

    /// <summary>Every value in <paramref name="model"/> that is not an object or an array, by its path</summary>
    public static Dictionary<string, JsonNode?> Flatten(JsonNode? model)
    {
        Dictionary<string, JsonNode?> values = [];
        void Visit(string path, JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((string key, JsonNode? value) in obj)
                    {
                        Visit(path.Length == 0 ? key : $"{path}.{key}", value);
                    }
                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        Visit($"{path}[{i}]", array[i]);
                    }
                    break;
                default:
                    values[path] = node;
                    break;
            }
        }
        Visit(string.Empty, model);
        return values;
    }

    /// <summary>Whether a path is one <paramref name="patterns"/> names, or under one (<c>[*]</c> matches any index)</summary>
    public static bool Matches(string path, IEnumerable<string> patterns) => new PathFilter(patterns).Ignores(path);

    private static JsonNode? Prune(string path, JsonNode? node, PathFilter filter)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject result = [];
                foreach ((string key, JsonNode? value) in obj)
                {
                    string child = path.Length == 0 ? key : $"{path}.{key}";
                    if (!filter.Ignores(child))
                    {
                        result[key] = Prune(child, value, filter);
                    }
                }
                return result;
            case JsonArray array:
                return new JsonArray([.. array.Select((value, i) => Prune($"{path}[{i}]", value, filter))]);
            default:
                return node?.DeepClone();
        }
    }

    private static void Walk(string path, JsonNode? before, JsonNode? after, List<ObjectModelChange> changes, PathFilter filter)
    {
        if (filter.Ignores(path))
        {
            return;
        }
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (string key in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
            {
                Walk(path.Length == 0 ? key : $"{path}.{key}", a[key], b[key], changes, filter);
            }
            return;
        }
        if (before is JsonArray x && after is JsonArray y)
        {
            for (int i = 0; i < x.Count || i < y.Count; i++)
            {
                Walk($"{path}[{i}]", i < x.Count ? x[i] : null, i < y.Count ? y[i] : null, changes, filter);
            }
            return;
        }
        if (!JsonNode.DeepEquals(before, after))
        {
            changes.Add(new ObjectModelChange(path, before?.DeepClone(), after?.DeepClone()));
        }
    }

    // Paths to leave out, compared part by part so that [*] matches any index
    private sealed class PathFilter(IEnumerable<string> paths)
    {
        private readonly string[][] _patterns = [.. paths.Select(Split)];

        public bool Ignores(string path)
        {
            if (_patterns.Length == 0 || path.Length == 0)
            {
                return false;
            }
            string[] parts = Split(path);
            return _patterns.Any(pattern => pattern.Length <= parts.Length &&
                                            pattern.Select((part, i) => part == parts[i] || (part == "[*]" && parts[i].StartsWith('['))).All(m => m));
        }

        // boards[1].drivers[0] -> boards, [1], drivers, [0]
        private static string[] Split(string path) =>
            [.. path.Replace("[", ".[").Split('.', System.StringSplitOptions.RemoveEmptyEntries)];
    }
}
