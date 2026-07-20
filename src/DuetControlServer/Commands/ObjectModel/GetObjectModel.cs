using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.Command"/> command
/// </summary>
/// <param name="model">Object model</param>
/// <param name="filter">Filter for partial queries</param>
public sealed class GetObjectModel(Model.ObjectModel model, Model.Filter filter) : DuetAPI.Commands.GetObjectModel, IRawJsonCommand
{
    /// <summary>
    /// Retrieve a copy of the current machine model
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Clone of the current machine model or a partial one if filters are set</returns>
    public override async Task<ObjectModel> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Filters.Count == 0)
            {
                return (ObjectModel)model.Clone();
            }

            ObjectModel result = new();
            using JsonDocument filteredJson = GetFilteredJson();
            result.UpdateFromJson(filteredJson.RootElement, false);
            return result;
        }
    }

    /// <summary>
    /// Retrieve the current machine model as UTF-8 JSON without cloning it first
    /// </summary>
    /// <param name="destination">Buffer writer to write the serialized machine model (or a partial one if filters are set) to</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask ExecuteRawJsonAsync(IBufferWriter<byte> destination, CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            using Utf8JsonWriter writer = new(destination);
            if (Filters.Count == 0)
            {
                JsonSerializer.Serialize<ObjectModel>(writer, model, JsonHelper.DefaultJsonOptions);
            }
            else
            {
                ObjectModel result = new();
                using JsonDocument filteredJson = GetFilteredJson();
                result.UpdateFromJson(filteredJson.RootElement, false);
                JsonSerializer.Serialize<ObjectModel>(writer, result, JsonHelper.DefaultJsonOptions);
            }
        }
    }

    /// <summary>
    /// Collect only the requested parts so the whole model does not have to be cloned.
    /// The caller must hold a read lock on the object model
    /// </summary>
    /// <returns>Filtered machine model as JSON document</returns>
    private JsonDocument GetFilteredJson()
    {
        Dictionary<string, object?> filteredModel = [];
        foreach (object[] convertedFilter in Model.Filter.ConvertFilters(Filters))
        {
            Model.Filter.MergeFiltered(filteredModel, filter.GetFiltered(convertedFilter));
        }
        return JsonSerializer.SerializeToDocument(filteredModel, JsonHelper.DefaultJsonOptions);
    }
}
