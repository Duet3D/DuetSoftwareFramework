using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DuetCanMessage.SourceGenerators.Model;

/// <summary>
/// Checks the schema document against the JSON Schema that describes it, before anything reads it.
/// </summary>
/// <remarks>
/// <see cref="CanSchema"/> only reads the keys it knows about, so a misspelt one is not an error to it: it
/// simply does nothing, and the omission shows up as a missing accessor or a silently wrong layout much
/// later. Validating first turns that back into a message naming the offending line. It also catches the
/// values that have to be written as JSON strings even though they look like numbers - a constant's
/// <c>value</c>, an array's <c>length</c> - which would otherwise surface as an unhelpful cast failure.
/// </remarks>
public static class SchemaValidator
{
    /// <summary>The JSON Schema, expected next to the document it describes.</summary>
    private const string DefaultSchemaFile = "can-messages.schema.json";

    /// <summary>How many problems to report before summarising the rest.</summary>
    private const int MaxReportedErrors = 25;

    /// <summary>
    /// Keywords whose failure only says that something below them failed. The thing itself is reported when
    /// the walk reaches it, so repeating "some properties did not match" at every level above it is noise.
    /// </summary>
    private static readonly HashSet<string> AggregateKeywords =
        ["properties", "items", "prefixItems", "allOf", "dependentSchemas", "if", "then", "else"];

    /// <summary>
    /// Validate a parsed schema document, throwing <see cref="InvalidDataException"/> if it does not conform.
    /// </summary>
    /// <param name="documentPath">Path the document was read from, which is what the schema is found relative to.</param>
    /// <param name="document">The parsed document.</param>
    public static void Validate(string documentPath, JsonNode document)
    {
        string schemaPath = SchemaPathFor(documentPath, document);
        if (!File.Exists(schemaPath))
        {
            throw new InvalidDataException($"{schemaPath} is missing, so {Path.GetFileName(documentPath)} cannot be validated");
        }

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{schemaPath} is not valid JSON: {e.Message}");
        }

        // Hierarchical rather than List: the flat output reports the failed branches of every oneOf even
        // where one of them matched, which buries the real mistakes under thousands of lines about the
        // enum entries and statements that are perfectly fine.
        EvaluationResults results = schema.Evaluate(
            JsonSerializer.SerializeToElement(document),
            new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid)
        {
            return;
        }

        List<string> problems = [.. Problems(results)];
        string reported = string.Join('\n', problems.Take(MaxReportedErrors).Select(p => $"  {p}"));
        string remainder = problems.Count > MaxReportedErrors
            ? $"\n  ... and {problems.Count - MaxReportedErrors} more"
            : "";
        throw new InvalidDataException(
            $"{documentPath} does not match {Path.GetFileName(schemaPath)}:\n{reported}{remainder}");
    }

    /// <summary>
    /// Every failed assertion worth showing, as "location: what is wrong", in document order.
    /// </summary>
    private static IEnumerable<string> Problems(EvaluationResults node)
    {
        if (node.IsValid)
        {
            yield break;
        }

        string location = node.InstanceLocation.ToString();
        foreach (KeyValuePair<string, string> error in node.Errors ?? [])
        {
            // An empty keyword is the "false schema" that an unexpected property lands on; the property is
            // already named by the additionalProperties failure recorded on its parent.
            if (error.Key.Length > 0 && !AggregateKeywords.Contains(error.Key))
            {
                // A failed "not" says only that something matched which should not have, so name the rule
                // that rejected it; every other keyword's message identifies the problem on its own.
                string rule = error.Key == "not" ? $" (rule {node.SchemaLocation.Fragment.TrimStart('#')})" : "";
                yield return $"{(location.Length > 0 ? location : "/")}: {error.Value}{rule}";
            }
        }

        foreach (EvaluationResults child in FailingChildren(node).Where(child => !IsCondition(child)))
        {
            foreach (string problem in Problems(child))
            {
                yield return problem;
            }
        }
    }

    /// <summary>
    /// True for the <c>if</c> half of a conditional, whose failure is how the <c>else</c> branch gets
    /// selected rather than something wrong with the document.
    /// </summary>
    private static bool IsCondition(EvaluationResults node) =>
        node.EvaluationPath.ToString().EndsWith("/if", StringComparison.Ordinal);

    /// <summary>
    /// The failed children to descend into: all of them, except that only the closest branch of a failed
    /// <c>oneOf</c> or <c>anyOf</c> is followed.
    /// </summary>
    /// <remarks>
    /// Every branch of a union fails when the value matches none of them, and each complains that the value
    /// is not what its own branch describes. Reporting all of that is worse than useless - it describes the
    /// forms the author did not mean alongside the one they did - so the branch that got closest is taken as
    /// the intended one and only its complaints are shown.
    /// </remarks>
    private static IEnumerable<EvaluationResults> FailingChildren(EvaluationResults node)
    {
        List<EvaluationResults> failed = [.. (node.Details ?? []).Where(child => !child.IsValid)];
        if (node.Errors is null || !(node.Errors.ContainsKey("oneOf") || node.Errors.ContainsKey("anyOf")))
        {
            return failed;
        }

        string path = node.EvaluationPath.ToString();
        ILookup<bool, EvaluationResults> byKind = failed.ToLookup(child =>
            child.EvaluationPath.ToString().StartsWith($"{path}/oneOf/", StringComparison.Ordinal) ||
            child.EvaluationPath.ToString().StartsWith($"{path}/anyOf/", StringComparison.Ordinal));

        EvaluationResults? closest = byKind[true].OrderBy(ProblemCount).FirstOrDefault();
        return closest is null ? byKind[false] : byKind[false].Append(closest);
    }

    /// <summary>How many assertions failed in a subtree, used to pick the branch a value came closest to matching.</summary>
    private static int ProblemCount(EvaluationResults node) =>
        (node.Errors?.Count ?? 0) + (node.Details ?? []).Where(child => !child.IsValid).Sum(ProblemCount);

    /// <summary>
    /// Where to look for the JSON Schema: the document's <c>$schema</c> when it is a relative path, and the
    /// file beside the document otherwise.
    /// </summary>
    /// <remarks>
    /// A <c>$schema</c> written as an absolute URI names the schema rather than locating a copy of it, and
    /// nothing here goes to the network to fetch one, so that case falls back to the local file.
    /// </remarks>
    private static string SchemaPathFor(string documentPath, JsonNode document)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? ".";
        string? declared = document is JsonObject o && o["$schema"] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;
        return Path.GetFullPath(declared is not null && !Uri.IsWellFormedUriString(declared, UriKind.Absolute)
            ? Path.Combine(directory, declared)
            : Path.Combine(directory, DefaultSchemaFile));
    }
}
