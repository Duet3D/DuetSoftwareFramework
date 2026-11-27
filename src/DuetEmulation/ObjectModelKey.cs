using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DuetEmulation;

/// <summary>
/// Object model keys as M409 takes them, such as <c>boards[1].drivers[0].config.direction</c>, applied
/// to the object model as JSON
/// </summary>
public static partial class ObjectModelKey
{
    /// <summary>
    /// The part of <paramref name="model"/> a key names, or null if the model has nothing there; a part
    /// within the model is copied, so that it can go into another document
    /// </summary>
    /// <exception cref="FormatException">The key is not a sequence of names with optional indices</exception>
    public static JsonNode? Select(JsonNode? model, string key)
    {
        JsonNode? node = model;
        if (key.Length == 0)
        {
            return node;
        }
        foreach (string part in key.Split('.'))
        {
            Match match = PartPattern().Match(part);
            if (!match.Success)
            {
                throw new FormatException($"\"{key}\" is not an object model key such as boards[1].drivers[0]");
            }
            node = node is JsonObject obj ? obj[match.Groups["name"].Value] : null;
            foreach (Capture index in match.Groups["index"].Captures)
            {
                int i = int.Parse(index.Value, CultureInfo.InvariantCulture);
                node = node is JsonArray array && i < array.Count ? array[i] : null;
            }
            if (node is null)
            {
                return null;
            }
        }
        return node.DeepClone();
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(\[(?<index>\d+)\])*$")]
    private static partial Regex PartPattern();
}
