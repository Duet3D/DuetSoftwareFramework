using DuetAPI.Utility;
using DuetControlServer.Model;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.QueryObjectModel"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="filter">Filter for JSON queries</param>
public sealed class QueryObjectModel(Model.ObjectModel model, Filter filter) : DuetAPI.Commands.QueryObjectModel, IRawJsonCommand
{
    /// <summary>
    /// Query the object model using a key and flags
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>JSON response compatible with M409 format</returns>
    public override async Task<JsonElement> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> jsonBuffer = new();
        await ExecuteRawJsonAsync(jsonBuffer, cancellationToken);
        using JsonDocument response = JsonDocument.Parse(jsonBuffer.WrittenMemory);
        return response.RootElement.Clone();
    }

    /// <summary>
    /// Query the object model using a key and flags returning UTF-8 JSON
    /// </summary>
    /// <param name="destination">Buffer writer to write the JSON response to</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask ExecuteRawJsonAsync(IBufferWriter<byte> destination, CancellationToken cancellationToken = default)
    {
        string key = Key;
        string flags = Flags;

        // Parse flags
        QueryFlags queryFlags = QueryFlags.Parse(flags);
        if (string.IsNullOrEmpty(key) && !flags.Contains('d'))
        {
            // RRF reports the whole model only to the first level unless an explicit depth is requested, see ObjectModel::ReportAsJson
            queryFlags = queryFlags with { MaxDepth = 1 };
        }
        bool includeNulls = flags.Contains('n');
        JsonSerializerOptions jsonOptions = includeNulls ? JsonHelper.DefaultJsonOptions : JsonHelper.NoNullJsonOptions;
        JsonTypeInfo dictionaryTypeInfo = jsonOptions.GetTypeInfo(typeof(Dictionary<string, object?>));

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // Build the filter expression
            string filterExpression = string.IsNullOrEmpty(key) ? "**" : key + ".**";

            // Retrieve filtered OM data
            using JsonDocument queryResult = JsonSerializer.SerializeToDocument(filter.GetFiltered(filterExpression, queryFlags), dictionaryTypeInfo);

            // Get down to the requested depth
            JsonElement result = queryResult.RootElement;
            bool keyResolved = true;
            if (!string.IsNullOrEmpty(key))
            {
                foreach (string depth in key.Split('.'))
                {
                    if (result.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement.ObjectEnumerator subItems = result.EnumerateObject();
                        if (!subItems.MoveNext())
                        {
                            keyResolved = false;
                            break;
                        }
                        result = subItems.Current.Value;
                    }
                }
            }

            // For root-level queries, inject sequence numbers into the result
            object finalResult;
            if (string.IsNullOrEmpty(key) && result.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, object?> resultDict = (Dictionary<string, object?>?)result.Deserialize(dictionaryTypeInfo) ?? [];
                Dictionary<string, object?> seqs = [];
                foreach (var kvp in model.Seqs)
                {
                    seqs[kvp.Key] = kvp.Value;
                }
                resultDict["seqs"] = seqs;
                finalResult = resultDict;
            }
            else
            {
                finalResult = result;
            }

            // Build response object
            Dictionary<string, object?> response = new()
            {
                ["key"] = key,
                ["flags"] = flags
            };

            // Add result with optional "next" for arrays
            if (!keyResolved)
            {
                // RRF reports null if the key does not exist, see the M409 section of the G-code dictionary
                response["result"] = null;
            }
            else if (result.ValueKind == JsonValueKind.Array)
            {
                // Apply array pagination if 'a' flag was specified
                if (queryFlags.StartElement > 0)
                {
                    var elements = result.EnumerateArray().Skip(queryFlags.StartElement).ToList();
                    response["result"] = elements;
                }
                else
                {
                    response["result"] = result;
                }
                response["next"] = 0;
            }
            else
            {
                response["result"] = finalResult;
            }

            using Utf8JsonWriter writer = new(destination);
            JsonSerializer.Serialize(writer, response, dictionaryTypeInfo);
        }
    }
}
