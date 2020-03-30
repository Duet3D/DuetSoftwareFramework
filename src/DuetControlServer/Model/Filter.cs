using DuetAPI.Machine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Provides filter functionality to get partial object model data
    /// </summary>
    public static class Filter
    {
        /// <summary>
        /// Regular expressinon to extract name and index from a filter item
        /// </summary>
        private static readonly Regex _indexRegex = new Regex(@"(.*)\[([\d,*]+)\]");

        /// <summary>
        /// Convert multiple filter strings into an object array that can be used to traverse the object model
        /// </summary>
        /// <param name="filters">Delimited filter expressions</param>
        /// <returns>Object array</returns>
        public static object[][] ConvertFilters(string filters)
        {
            string[] filterStrings = filters.Split(',', '|', '\r', '\n', ' ');
            List<object[]> convertedFilters = new List<object[]>();
            for (int i = 0; i < filterStrings.Length; i++)
            {
                object[] convertedFilter = ConvertFilter(filterStrings[i], false);
                if (convertedFilter.Length > 0)
                {
                    convertedFilters.Add(convertedFilter);
                }
            }
            return convertedFilters.ToArray();
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
            List<object> filterItems = new List<object>();
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
            return filterItems.ToArray();
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
            for (int i = 0; i < path.Length; i++)
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

                    if (path[i] is string pathString)
                    {
                        if (filterString != "*" && !filterString.Equals(pathString, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // Not the property we're looking for
                            return false;
                        }
                    }
                    else if (path[i] is ItemPathNode pathNode)
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
        public static Dictionary<string, object> GetFiltered(object[] filter) => (Dictionary<string, object>)InternalGetFiltered(Provider.Get, filter);

        /// <summary>
        /// Internal function to find a specific object in the object model
        /// </summary>
        /// <param name="partialModel">Partial object model</param>
        /// <param name="partialFilter">Array consisting of item indices or case-insensitive property names</param>
        /// <returns>Dictionary or list holding the result or null if nothing could be found</returns>
        private static object InternalGetFiltered(object partialModel, object[] partialFilter)
        {
            // Cannot proceed if there is nothing more to do...
            if (partialModel == null || partialFilter.Length == 0)
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
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, PropertyInfo> property in model.JsonProperties)
                    {
                        if (propertyName == "*" || property.Key == propertyName)
                        {
                            if (partialFilter.Length == 0 ||
                                (partialFilter.Length == 1 && partialFilter[0] is string partialStringFilter && partialStringFilter == "**"))
                            {
                                // This is a property we've been looking for
                                result.Add(property.Key, property.Value.GetValue(model));
                                continue;
                            }

                            if (property.Value.PropertyType.IsSubclassOf(typeof(ModelObject)) ||
                                typeof(IList).IsAssignableFrom(property.Value.PropertyType))
                            {
                                // Property is somewhere deeper
                                object propertyValue = property.Value.GetValue(model);
                                object subResult = InternalGetFiltered(propertyValue, partialFilter);
                                if (subResult != null)
                                {
                                    result.Add(property.Key, subResult);
                                }
                            }
                        }
                    }
                    return (result.Count != 0) ? result : null;
                }
            }
            else if (currentFilter is int itemIndex)
            {
                if (partialModel is IList list && itemIndex >= -1 && itemIndex < list.Count)
                {
                    bool isModelObjectList = false, isListList = false;
                    if (ModelCollection.GetItemType(partialModel.GetType(), out Type itemType))
                    {
                        isModelObjectList = itemType.IsSubclassOf(typeof(ModelObject));
                        isListList = typeof(IList).IsAssignableFrom(itemType);
                    }
                    
                    // If this is a value list or the list we've been looking for, return it immediately
                    if ((!isModelObjectList && !isListList) || (itemIndex == -1 && partialFilter.Length == 0))
                    {
                        return list;
                    }

                    // This is an object list, return either the filter results or dummy objects
                    List<object> results = new List<object>(new object[list.Count]);
                    for (int i = 0; i < list.Count; i++)
                    {
                        object item = list[i];
                        if (itemIndex == -1 || i == itemIndex)
                        {
                            if (partialFilter.Length == 0)
                            {
                                // This is one of the items we've been looking for
                                results[i] = item;
                            }
                            else if (item != null)
                            {
                                // Property is somewhere deeper
                                object subResult = InternalGetFiltered(item, partialFilter);
                                if (subResult != null)
                                {
                                    // Got a result
                                    results[i] = subResult;
                                }
                                else
                                {
                                    // Set placeholder
                                    results[i] = isModelObjectList ? (object)new Dictionary<string, object>() : new List<object>();
                                }
                            }
                        }
                        else if (item != null)
                        {
                            // Set placeholder
                            results[i] = isModelObjectList ? (object)new Dictionary<string, object>() : new List<object>();
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
        public static void MergeFiltered(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (b == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> item in b)
            {
                if (a.TryGetValue(item.Key, out object aItem))
                {
                    // Item already exists, try to merge it
                    if (aItem is Dictionary<string, object> aDictionary)
                    {
                        if (item.Value is Dictionary<string, object> bDictionary)
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
            if (b == null || a.Count != b.Count)
            {
                return;
            }

            for (int i = 0; i < b.Count; i++)
            {
                if (a[i] is Dictionary<string, object> aDictionary)
                {
                    if (b[i] is Dictionary<string, object> bDictionary)
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
        /// <param name="findLinuxProperty">Whether the object may be a Linux property</param>
        /// <param name="result">Partial object model or null</param>
        /// <returns>Whether the object could be found</returns>
        public static bool GetSpecific(string filter, bool findLinuxProperty, out object result)
        {
            return InternalGetSpecific(Provider.Get, ConvertFilter(filter, false), findLinuxProperty, false, out result);
        }

        /// <summary>
        /// Internal function to find a specific object in the object model
        /// </summary>
        /// <param name="partialModel">Partial object model</param>
        /// <param name="partialFilter">Array consisting of item indices or case-insensitive property names</param>
        /// <param name="findLinuxProperty">Whether the object may be a Linux property</param>
        /// <param name="hadLinuxProperty">Whether a Linux property is part of the current node path</param>
        /// <param name="result">Partial object model or null</param>
        /// <returns>Whether the object could be found</returns>
        private static bool InternalGetSpecific(object partialModel, object[] partialFilter, bool findLinuxProperty, bool hadLinuxProperty, out object result)
        {
            // Cannot proceed if there is nothing more to do...
            if (partialModel == null || partialFilter.Length == 0)
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
                    if (model.JsonProperties.TryGetValue(propertyName, out PropertyInfo property))
                    {
                        if (findLinuxProperty && Attribute.IsDefined(property, typeof(LinuxPropertyAttribute)))
                        {
                            hadLinuxProperty = true;
                        }

                        if (partialFilter.Length == 0)
                        {
                            if (!findLinuxProperty || hadLinuxProperty)
                            {
                                // This is exactly the property we've been looking for
                                result = property.GetValue(model);
                                return true;
                            }
                        }
                        else if (property.PropertyType.IsSubclassOf(typeof(ModelObject)) || typeof(IList).IsAssignableFrom(property.PropertyType))
                        {
                            // Property is somewhere deeper
                            object propertyValue = property.GetValue(model);
                            return InternalGetSpecific(propertyValue, partialFilter, findLinuxProperty, hadLinuxProperty, out result);
                        }
                    }
                }
            }
            else if (partialFilter[0] is int itemIndex && (!findLinuxProperty || hadLinuxProperty))
            {
                partialFilter = partialFilter.Skip(1).ToArray();
                if (partialModel is IList list)
                {
                    if (itemIndex >= 0 && itemIndex < list.Count)
                    {
                        object item = list[itemIndex];
                        if (partialFilter.Length == 0)
                        {
                            // This is the item we've been looking for
                            result = item;
                            return true;
                        }

                        if (item is ModelObject || item is IList)
                        {
                            // Property is somewhere deeper
                            return InternalGetSpecific(item, partialFilter, findLinuxProperty, hadLinuxProperty, out result);
                        }
                    }
                }
            }

            // Nothing found
            result = null;
            return false;
        }
    }
}
