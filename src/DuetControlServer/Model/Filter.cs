using DuetAPI.ObjectModel;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DuetControlServer.Model;

/// <summary>
/// Flags parsed from a query flags string that control which properties are included in filtered results
/// </summary>
public readonly struct QueryFlags()
{
    /// <summary>
    /// If true, only include properties marked with <see cref="LiveAttribute"/>
    /// </summary>
    public bool LiveOnly { get; init; }

    /// <summary>
    /// If true, include properties marked with <see cref="VerboseAttribute"/>
    /// </summary>
    public bool IncludeVerbose { get; init; }

    /// <summary>
    /// If true, include properties marked with <see cref="ObsoleteAttribute"/>
    /// </summary>
    public bool IncludeObsolete { get; init; }

    /// <summary>
    /// If true, include null values in the result
    /// </summary>
    public bool IncludeNulls { get; init; }

    /// <summary>
    /// Maximum recursion depth for sub-objects. Default is 99 (effectively unlimited).
    /// With 'd1' only top-level properties are returned, sub-objects become empty objects
    /// </summary>
    public int MaxDepth { get; init; } = 99;

    /// <summary>
    /// Start element index for root-level array results (used for pagination).
    /// Default is 0 (start from the beginning)
    /// </summary>
    public int StartElement { get; init; }

    /// <summary>
    /// Parse a flags string into a <see cref="QueryFlags"/> instance
    /// </summary>
    /// <param name="flags">RRF-compatible flags string</param>
    /// <returns>Parsed query flags</returns>
    public static QueryFlags Parse(string? flags)
    {
        bool liveOnly = false, includeVerbose = false, includeObsolete = false, includeNulls = false;
        int maxDepth = 99, startElement = 0;
        if (flags is not null)
        {
            for (int i = 0; i < flags.Length; i++)
            {
                switch (flags[i])
                {
                    case 'a':
                        startElement = 0;
                        while (i + 1 < flags.Length && char.IsDigit(flags[i + 1]))
                        {
                            startElement = (10 * startElement) + (flags[++i] - '0');
                        }
                        break;
                    case 'd':
                        maxDepth = 0;
                        while (i + 1 < flags.Length && char.IsDigit(flags[i + 1]))
                        {
                            maxDepth = (10 * maxDepth) + (flags[++i] - '0');
                        }
                        break;
                    case 'f':
                        liveOnly = true;
                        break;
                    case 'n':
                        includeNulls = true;
                        break;
                    case 'o':
                        includeObsolete = true;
                        break;
                    case 'v':
                        includeVerbose = true;
                        break;
                }
            }
        }
        return new QueryFlags { LiveOnly = liveOnly, IncludeVerbose = includeVerbose, IncludeObsolete = includeObsolete, IncludeNulls = includeNulls, MaxDepth = maxDepth, StartElement = startElement };
    }

    /// <summary>
    /// Check if a property should be included based on its attributes and these flags
    /// </summary>
    /// <param name="property">Property to check</param>
    /// <returns>Whether the property should be included</returns>
    public bool ShouldInclude(PropertyInfo property)
    {
        if (LiveOnly && !Attribute.IsDefined(property, typeof(LiveAttribute)))
        {
            return false;
        }
        if (!IncludeVerbose && Attribute.IsDefined(property, typeof(VerboseAttribute)))
        {
            return false;
        }
        if (!IncludeObsolete && Attribute.IsDefined(property, typeof(ObsoleteAttribute)))
        {
            return false;
        }
        return true;
    }
}

/// <summary>
/// Provides filter functionality to get partial object model data
/// </summary>
public partial class Filter(ObjectModel model)
{
    [GeneratedRegex(@"(.*)\[([\d,*]+)\]")]
    private static partial Regex _generateIndexRegex();

    /// <summary>
    /// Regular expression to extract name and index from a filter item
    /// </summary>
    private static readonly Regex _indexRegex = _generateIndexRegex();

    /// <summary>
    /// Convert delimited filter strings into an object array that can be used to traverse the object model
    /// </summary>
    /// <param name="filters">Delimited filter expressions</param>
    /// <returns>Object array</returns>
    public static object[][] ConvertFilters(string filters)
    {
        string[] filterStrings = filters.Split(',', '|', '\r', '\n', ' ');
        return ConvertFilters(filterStrings);
    }

    /// <summary>
    /// Convert multiple filter strings into an object array that can be used to traverse the object model
    /// </summary>
    /// <param name="filters">Delimited filter expressions</param>
    /// <returns>Object array</returns>
    public static object[][] ConvertFilters(IEnumerable<string> filters)
    {
        List<object[]> convertedFilters = [];
        foreach (string filter in filters)
        {
            object[] convertedFilter = ConvertFilter(filter, false);
            if (convertedFilter.Length > 0)
            {
                convertedFilters.Add(convertedFilter);
            }
        }
        return [.. convertedFilters];
    }
    /// <summary>
    /// Convert a filter string into an object array that can be used to traverse the object model
    /// </summary>
    /// <param name="filter">Filter expression</param>
    /// <param name="codeExpression">Whether the filter is from a G-code expression</param>
    /// <returns>Object array</returns>
    public static object[] ConvertFilter(string filter, bool codeExpression)
    {
        return codeExpression ? ConvertFilter(filter.Split('.')) : ConvertFilter(filter.Split('.', '/'));
    }

    /// <summary>
    /// Convert filter string items into an object array that can be used to traverse the object model
    /// </summary>
    /// <param name="filter">Filter expression</param>
    /// <returns>Object array</returns>
    public static object[] ConvertFilter(string[] filter)
    {
        List<object> filterItems = [];
        foreach (string filterItem in filter)
        {
            Match match = _indexRegex.Match(filterItem);
            if (match.Success && match.Groups.Count > 2)
            {
                string propertyName = match.Groups[1].Value;
                filterItems.Add(propertyName);
                if (match.Groups[2].Value == "*")
                {
                    filterItems.Add(-1);
                }
                else
                {
                    int itemIndex = int.Parse(match.Groups[2].Value);
                    filterItems.Add(itemIndex);
                }
            }
            else
            {
                filterItems.Add(filterItem);
            }
        }
        return [.. filterItems];
    }

    /// <summary>
    /// Checks if a change path matches a given filter
    /// </summary>
    /// <param name="path">Patch path</param>
    /// <param name="filter">Path filter</param>
    /// <returns>True if a filter applies</returns>
    public static bool PathMatches(object[] path, object[] filter)
    {
        int filterIndex = 0;
        foreach (object pathItem in path)
        {
            if (filterIndex >= filter.Length)
            {
                // This is not the exact property path we're looking for
                return false;
            }

            if (filter[filterIndex++] is string filterString)
            {
                if (filterString == "**")
                {
                    // This is what we're looking for
                    return true;
                }

                if (pathItem is string pathString)
                {
                    if (filterString != "*" && !filterString.Equals(pathString, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Not the property we're looking for
                        return false;
                    }
                }
                else if (pathItem is ItemPathNode pathNode)
                {
                    int itemIndex = -1;
                    if (filterIndex < filter.Length && filter[filterIndex] is int intFilter)
                    {
                        // Index is present, use it
                        itemIndex = intFilter;
                        filterIndex++;
                    }

                    if ((filterString != "*" && !pathNode.Name.Equals(filterString)) || (itemIndex != -1 && itemIndex != pathNode.Index))
                    {
                        // Not the item we're looking for
                        return false;
                    }
                }
            }
            else
            {
                // Indices must always follow property names
                return false;
            }
        }

        // This must be exactly the property we're looking for
        return true;
    }

    /// <summary>
    /// Get a partial object model with only fields that match the given filter
    /// </summary>
    /// <param name="filter">Array consisting of case-insensitive property names or item indices</param>
    /// <returns>Dictionary holding the results or null if nothing could be found</returns>
    /// <remarks>Make sure the model provider is locked in read-only mode before using this class</remarks>
    /// <seealso cref="DuetAPI.Connection.InitMessages.SubscribeInitMessage.Filter"/>
    public Dictionary<string, object?> GetFiltered(object[] filter) => (Dictionary<string, object?>?)InternalGetFiltered(model, filter, null) ?? [];

    /// <summary>
    /// Get a partial object model with only fields that match the given filter
    /// </summary>
    /// <param name="filter">Filter string</param>
    /// <returns>Dictionary holding the results or null if nothing could be found</returns>
    /// <remarks>Make sure the model provider is locked in read-only mode before using this class</remarks>
    /// <seealso cref="DuetAPI.Connection.InitMessages.SubscribeInitMessage.Filter"/>
    public Dictionary<string, object?> GetFiltered(string filter) => (Dictionary<string, object?>?)InternalGetFiltered(model, ConvertFilter(filter, false), null) ?? [];

    /// <summary>
    /// Get a partial object model with only fields that match the given filter and query flags
    /// </summary>
    /// <param name="filter">Filter string</param>
    /// <param name="queryFlags">Flags controlling which properties are included</param>
    /// <returns>Dictionary holding the results or null if nothing could be found</returns>
    /// <remarks>Make sure the model provider is locked in read-only mode before using this class</remarks>
    public Dictionary<string, object?> GetFiltered(string filter, QueryFlags queryFlags)
    {
        return (Dictionary<string, object?>?)InternalGetFiltered(model, ConvertFilter(filter, false), queryFlags, 0) ?? [];
    }

    /// <summary>
    /// Internal function to find a specific object in the object model
    /// </summary>
    /// <param name="partialModel">Partial object model</param>
    /// <param name="partialFilter">Array consisting of item indices or case-insensitive property names</param>
    /// <param name="queryFlags">Optional flags controlling which properties are included, or null for no attribute filtering</param>
    /// <param name="depth">Current recursion depth (used with <see cref="QueryFlags.MaxDepth"/>)</param>
    /// <returns>Dictionary or list holding the result or null if nothing could be found</returns>
    private static object? InternalGetFiltered(object? partialModel, object[] partialFilter, QueryFlags? queryFlags, int depth = 0)
    {
        // Cannot proceed if there is nothing more to do...
        if (partialModel is null || partialFilter.Length == 0)
        {
            return null;
        }
        object currentFilter = partialFilter[0];
        partialFilter = partialFilter.Skip(1).ToArray();

        // Check what kind of item to expect
        if (currentFilter is string propertyName)
        {
            if (partialModel is ModelObject model)
            {
                // Check if we've exceeded the maximum depth
                if (queryFlags is not null && depth >= queryFlags.Value.MaxDepth)
                {
                    return new Dictionary<string, object?>();
                }

                Dictionary<string, object?> result = [];
                foreach (PropertyInfo property in model.GetType().GetProperties(BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance))
                {
                    string jsonPropertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    if (propertyName is "*" or "**" || propertyName == jsonPropertyName)
                    {
                        // Check if property should be included based on its attributes
                        if (queryFlags is not null && !queryFlags.Value.ShouldInclude(property))
                        {
                            continue;
                        }

                        if (partialFilter.Length == 0 ||
                            (partialFilter.Length == 1 && partialFilter[0] is "**") ||
                            propertyName == "**")
                        {
                            // When using "**" with query flags, recurse into sub-objects
                            // so that attribute-based filtering is applied at every level
                            if (propertyName == "**" && queryFlags is not null)
                            {
                                object? propertyValue = property.GetValue(model);
                                if (propertyValue is ModelObject || propertyValue is IList)
                                {
                                    object? subResult = InternalGetFiltered(propertyValue, ["**"], queryFlags, depth + 1);
                                    if (subResult is not null)
                                    {
                                        result.Add(jsonPropertyName, subResult);
                                    }
                                }
                                else if (propertyValue is not null || queryFlags.Value.IncludeNulls)
                                {
                                    result.Add(jsonPropertyName, propertyValue);
                                }
                            }
                            else
                            {
                                // This is a property we've been looking for
                                result.Add(jsonPropertyName, property.GetValue(model));
                            }
                            continue;
                        }

                        if (property.PropertyType.IsSubclassOf(typeof(ModelObject)) || typeof(IList).IsAssignableFrom(property.PropertyType))
                        {
                            // Property is somewhere deeper
                            object propertyValue = property.GetValue(model)!;
                            object? subResult = InternalGetFiltered(propertyValue, partialFilter, queryFlags, depth + 1);
                            if (subResult is not null)
                            {
                                result.Add(jsonPropertyName, subResult);
                            }
                        }
                    }
                }
                return (result.Count != 0) ? result : null;
            }
            else if (partialModel is IList filterList && propertyName == "**" && queryFlags is not null)
            {
                // Recurse into list items to apply attribute-based filtering
                bool isModelObjectList = filterList.GetType().IsGenericType &&
                    filterList.GetType().GetGenericArguments()[0].IsSubclassOf(typeof(ModelObject));
                if (isModelObjectList)
                {
                    List<object?> results = [.. new object?[filterList.Count]];
                    for (int i = 0; i < filterList.Count; i++)
                    {
                        object? item = filterList[i];
                        if (item is not null)
                        {
                            object? subResult = InternalGetFiltered(item, ["**"], queryFlags, depth);
                            results[i] = subResult ?? new Dictionary<string, object?>();
                        }
                    }
                    return results;
                }
                return filterList;
            }
        }
        else if (currentFilter is int itemIndex)
        {
            if (partialModel is IList list && itemIndex >= -1 && itemIndex < list.Count)
            {
                bool isModelObjectList = false, isListList = false;
                if (partialModel.GetType().IsGenericType && partialModel.GetType().GetGenericTypeDefinition() == typeof(StaticModelCollection<>))
                {
                    Type itemType = partialModel.GetType().GetGenericArguments()[0];
                    isModelObjectList = itemType.IsSubclassOf(typeof(ModelObject));
                    isListList = typeof(IList).IsAssignableFrom(itemType);
                }

                // If this is a value list or the list we've been looking for, return it immediately
                if ((!isModelObjectList && !isListList) || (itemIndex == -1 && partialFilter.Length == 0))
                {
                    return list;
                }

                // This is an object list, return either the filter results or dummy objects
                List<object?> results = [.. new object?[list.Count]];
                for (int i = 0; i < list.Count; i++)
                {
                    object? item = list[i];
                    if (itemIndex == -1 || i == itemIndex)
                    {
                        if (partialFilter.Length == 0)
                        {
                            // This is one of the items we've been looking for
                            results[i] = item;
                        }
                        else if (item is not null)
                        {
                            // Property is somewhere deeper
                            object? subResult = InternalGetFiltered(item, partialFilter, queryFlags);
                            if (subResult is not null)
                            {
                                // Got a result
                                results[i] = subResult;
                            }
                            else
                            {
                                // Set placeholder
                                results[i] = isModelObjectList ? new Dictionary<string, object?>() : new List<object?>();
                            }
                        }
                    }
                    else if (item is not null)
                    {
                        // Set placeholder
                        results[i] = isModelObjectList ? new Dictionary<string, object?>() : new List<object?>();
                    }
                }
                return results;
            }
        }

        // Nothing found
        return null;
    }

    /// <summary>
    /// Merge two filtered object models
    /// </summary>
    /// <param name="a">First partial object model</param>
    /// <param name="b">Second partial object model</param>
    public static void MergeFiltered(Dictionary<string, object?> a, Dictionary<string, object?> b)
    {
        if (b is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> item in b)
        {
            if (a.TryGetValue(item.Key, out object? aItem))
            {
                // Item already exists, try to merge it
                if (aItem is Dictionary<string, object?> aDictionary)
                {
                    if (item.Value is Dictionary<string, object?> bDictionary)
                    {
                        MergeFiltered(aDictionary, bDictionary);
                    }
                    else
                    {
                        a[item.Key] = item.Value;
                    }
                }
                else if (aItem is IList aList)
                {
                    if (item.Value is IList bList)
                    {
                        MergeFilteredLists(aList, bList);
                    }
                    else
                    {
                        a[item.Key] = item.Value;
                    }
                }
            }
            else
            {
                // Item does not exist yet, add it
                a.Add(item.Key, item.Value);
            }
        }
    }

    /// <summary>
    /// Merge two partial model lists
    /// </summary>
    /// <param name="a">First list</param>
    /// <param name="b">Second list</param>
    private static void MergeFilteredLists(IList a, IList b)
    {
        if (b is null || a.Count != b.Count)
        {
            return;
        }

        for (int i = 0; i < b.Count; i++)
        {
            if (a[i] is Dictionary<string, object?> aDictionary)
            {
                if (b[i] is Dictionary<string, object?> bDictionary)
                {
                    MergeFiltered(aDictionary, bDictionary);
                }
                else
                {
                    a[i] = b[i];
                }
            }
            else if (a[i] is IList aList)
            {
                if (b[i] is IList bList)
                {
                    MergeFilteredLists(aList, bList);
                }
                else
                {
                    a[i] = b[i];
                }
            }
        }
    }

    /// <summary>
    /// Find a specific object in the object model (wildcards are not supported)
    /// </summary>
    /// <param name="filter">Filter for finding a property or a list item</param>
    /// <param name="findSbcProperty">Whether the object may be an SBC property</param>
    /// <param name="result">Partial object model or null</param>
    /// <returns>Whether the object could be found</returns>
    public bool GetSpecific(string filter, bool findSbcProperty, out object? result)
    {
        return InternalGetSpecific(model, ConvertFilter(filter, false), findSbcProperty, false, out result);
    }

    /// <summary>
    /// Internal function to find a specific object in the object model
    /// </summary>
    /// <param name="partialModel">Partial object model</param>
    /// <param name="partialFilter">Array consisting of item indices or case-insensitive property names</param>
    /// <param name="findSbcProperty">Whether the object may be an SBC property</param>
    /// <param name="hadSbcProperty">Whether an SBC property is part of the current node path</param>
    /// <param name="result">Partial object model or null</param>
    /// <returns>Whether the object could be found</returns>
    private static bool InternalGetSpecific(object partialModel, object[] partialFilter, bool findSbcProperty, bool hadSbcProperty, out object? result)
    {
        // Cannot proceed if there is nothing more to do...
        if (partialModel is null || partialFilter.Length == 0)
        {
            result = null;
            return false;
        }

        // Check what kind of item to expect
        if (partialFilter[0] is string propertyName)
        {
            partialFilter = partialFilter.Skip(1).ToArray();
            if (partialModel is ModelObject model)
            {
                PropertyInfo? property = model.GetType().GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (property is not null)
                {
                    if (findSbcProperty && Attribute.IsDefined(property, typeof(SbcPropertyAttribute)))
                    {
                        hadSbcProperty = true;
                    }

                    if (partialFilter.Length == 0)
                    {
                        if (!findSbcProperty || hadSbcProperty)
                        {
                            // This is exactly the property we've been looking for
                            result = property.GetValue(model);
                            return true;
                        }
                    }
                    else if (property.PropertyType == typeof(JsonElement) ||
                                property.PropertyType.IsSubclassOf(typeof(ModelObject)) ||
                                typeof(IModelDictionary).IsAssignableFrom(property.PropertyType) ||
                                typeof(IList).IsAssignableFrom(property.PropertyType))
                    {
                        // Property is somewhere deeper
                        object propertyValue = property.GetValue(model)!;
                        return InternalGetSpecific(propertyValue, partialFilter, findSbcProperty, hadSbcProperty, out result);
                    }
                }
            }
            else if (partialModel is IModelDictionary dict && dict.Contains(propertyName))
            {
                object? dictItem = dict[propertyName];
                if (partialFilter.Length == 0)
                {
                    if (!findSbcProperty || hadSbcProperty)
                    {
                        // This is exactly the property we've been looking for
                        result = dictItem;
                        return true;
                    }
                }
                else if (dictItem is not null)
                {
                    Type dictItemType = dictItem.GetType();
                    if (dictItemType == typeof(JsonElement) || dictItemType.IsSubclassOf(typeof(ModelObject)) || typeof(IList).IsAssignableFrom(dictItemType))
                    {
                        // Property is somewhere deeper
                        return InternalGetSpecific(dictItem, partialFilter, findSbcProperty, hadSbcProperty, out result);
                    }
                }
            }
            else if (partialModel is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object && jsonElement.TryGetProperty(propertyName, out JsonElement jsonItem))
            {
                if (partialFilter.Length == 0)
                {
                    if (!findSbcProperty || hadSbcProperty)
                    {
                        // This is exactly the property we've been looking for
                        result = jsonItem.ValueKind switch
                        {
                            JsonValueKind.String => jsonItem.GetString(),
                            JsonValueKind.Number => jsonItem.TryGetInt32(out int intValue) ? intValue : jsonItem.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Object => jsonItem,
                            JsonValueKind.Array => jsonItem,
                            _ => null
                        };
                        return true;
                    }
                }
                else
                {
                    // Property is somewhere deeper
                    return InternalGetSpecific(jsonItem, partialFilter, findSbcProperty, hadSbcProperty, out result);
                }
            }
        }
        else if (partialFilter[0] is int itemIndex && (!findSbcProperty || hadSbcProperty))
        {
            partialFilter = partialFilter.Skip(1).ToArray();
            if (partialModel is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                partialModel = jsonElement.EnumerateArray().ToList();
            }
            if (partialModel is IList list)
            {
                if (itemIndex >= 0 && itemIndex < list.Count)
                {
                    object? item = list[itemIndex];
                    if (partialFilter.Length == 0)
                    {
                        // This is the item we've been looking for
                        if (item is JsonElement jsonItem)
                        {
                            result = jsonItem.ValueKind switch
                            {
                                JsonValueKind.String => jsonItem.GetString(),
                                JsonValueKind.Number => jsonItem.TryGetInt32(out int intValue) ? intValue : jsonItem.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Object => jsonItem,
                                JsonValueKind.Array => jsonItem,
                                _ => null
                            };
                        }
                        else
                        {
                            result = item;
                        }
                        return true;
                    }

                    if (item is ModelObject || item is IList)
                    {
                        // Property is somewhere deeper
                        return InternalGetSpecific(item, partialFilter, findSbcProperty, hadSbcProperty, out result);
                    }
                }
            }
        }

        // Nothing found
        result = null;
        return false;
    }
}
