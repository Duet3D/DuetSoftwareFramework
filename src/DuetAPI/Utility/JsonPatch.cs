using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Helper class to create and apply JSON diffs
    /// </summary>
    public static class JsonPatch
    {
        /// <summary>
        /// Create a JSON patch
        /// </summary>
        /// <typeparam name="T">Type of the objects to compare</typeparam>
        /// <param name="a">Old object</param>
        /// <param name="b">New object</param>
        /// <returns>Differences as UTF-8 or null both objects are equal</returns>
        public static byte[] Diff<T>(T a, T b)
        {
            Dictionary<string, object> diffs = DiffObject(a, b);
            if (diffs != null)
            {
                return JsonSerializer.SerializeToUtf8Bytes(diffs, JsonHelper.DefaultJsonOptions);
            }
            return null;
        }

        private static Dictionary<string, object> DiffObject<T>(T a, T b)
        {
            Dictionary<string, object> diffs = null;
            Type type = a.GetType();

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    continue;
                }

                object valueA = property.GetValue(a);
                object valueB = property.GetValue(b);
                if (valueA == null || valueB == null)
                {
                    if (valueA != valueB)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = valueB;
                    }
                }
                else if (valueA is IList)
                {
                    object diff = Attribute.IsDefined(property, typeof(JsonGrowingListAttribute))
                        ? DiffGrowingList((IList)valueA, (IList)valueB)
                        : DiffList((IList)valueA, (IList)valueB);
                    if (diff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = valueB;
                    }
                }
                else if (Type.GetTypeCode(property.PropertyType) == TypeCode.Object)
                {
                    object diff = DiffObject(valueA, valueB);
                    if (diff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = valueB;
                    }
                }
                else if (!valueA.Equals(valueB))
                {
                    if (diffs == null)
                    {
                        diffs = new Dictionary<string, object>();
                    }

                    string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    diffs[propertyName] = valueB;
                }
            }

            return diffs;
        }

        private static object DiffList(IList a, IList b)
        {
            bool hadDiffs = a.Count != b.Count;
            object[] diffs = new object[b.Count];

            Type itemType = a.GetType().GetGenericArguments().Single();
            if (itemType.IsPrimitive || itemType.IsValueType)
            {
                for (int i = 0; i < b.Count; i++)
                {
                    if (i < a.Count)
                    {
                        if (a[i] == null || b[i] == null)
                        {
                            if (a[i] != b[i])
                            {
                                hadDiffs = true;
                                diffs[i] = b[i];
                            }
                            else
                            {
                                diffs[i] = new object();
                            }
                        }
                        else if (!a[i].Equals(b[i]))
                        {
                            hadDiffs = true;
                            diffs[i] = b[i];
                        }
                        else
                        {
                            diffs[i] = new object();
                        }
                    }
                    else
                    {
                        diffs[i] = new object();
                    }
                }
            }
            else
            {
                for (int i = 0; i < b.Count; i++)
                {
                    if (i < a.Count)
                    {
                        if (a[i] == null || b[i] == null)
                        {
                            if (a[i] != b[i])
                            {
                                hadDiffs = true;
                                diffs[i] = b[i];
                            }
                            else
                            {
                                diffs[i] = new object();
                            }
                        }
                        else
                        {
                            object diff = DiffObject(a[i], b[i]);
                            if (diff != null)
                            {
                                hadDiffs = true;
                                diffs[i] = diff;
                            }
                            else
                            {
                                diffs[i] = new object();
                            }
                        }
                    }
                    else
                    {
                        diffs[i] = b[i];
                    }
                }
            }

            return hadDiffs ? diffs : null;
        }

        private static object DiffGrowingList(IList a, IList b)
        {
            if (a.Count == b.Count)
            {
                // The number of items has not changed
                return null;
            }

            if (b.Count < a.Count)
            {
                // If the new list has fewer items, this implies the list has been cleared
                return Array.Empty<object>();
            }

            // Get added items
            object[] diffs = new object[b.Count - a.Count];

            int index = 0;
            for (int i = a.Count; i < b.Count; i++)
            {
                diffs[index++] = b[i];
            }

            return diffs;
        }

        /// <summary>
        /// Apply JSON patch
        /// </summary>
        /// <param name="obj">Object to patch</param>
        /// <param name="diff">JSON diff</param>
        public static void Patch(object obj, JsonDocument diff) => PatchObject(obj, diff.RootElement);

        private static void PatchObject(object obj, JsonElement diff)
        {
            Type type = obj.GetType();
            foreach (JsonProperty item in diff.EnumerateObject())
            {
                PropertyInfo property = type.GetProperty(item.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property != null)
                {
                    if (item.Value.ValueKind == JsonValueKind.Null)
                    {
                        property.SetValue(obj, null);
                    }
                    else
                    {
                        Type propertyType = property.PropertyType;
                        object value = property.GetValue(obj);
                        if (value != null && Type.GetTypeCode(propertyType) == TypeCode.Object)
                        {
                            if (item.Value.ValueKind == JsonValueKind.Array)
                            {
                                IList list = (IList)property.GetValue(obj);
                                Type itemType = property.PropertyType.GetGenericArguments().Single();
                                if (Attribute.IsDefined(property, typeof(JsonGrowingListAttribute)))
                                {
                                    PatchCompactList(list, itemType, item.Value);
                                }
                                else
                                {
                                    PatchList(list, itemType, item.Value);
                                }
                            }
                            else
                            {
                                PatchObject(value, item.Value);
                            }
                        }
                        else
                        {
                            object newValue = JsonSerializer.Deserialize(item.Value.GetRawText(), propertyType, JsonHelper.DefaultJsonOptions);
                            property.SetValue(obj, newValue);
                        }
                    }
                }
            }
        }

        private static void PatchCompactList(IList list, Type itemType, JsonElement diff)
        {
            int arrayLength = diff.GetArrayLength();
            if (arrayLength == 0)
            {
                // If this value is present but empty, it means the list has been cleared
                list.Clear();
            }
            else
            {
                // Else add only new items
                foreach (var item in diff.EnumerateArray())
                {
                    object newItem = JsonSerializer.Deserialize(item.GetRawText(), itemType, JsonHelper.DefaultJsonOptions);
                    list.Add(newItem);
                }
            }
        }
        private static void PatchList(IList list, Type itemType, JsonElement diff)
        {
            int arrayLength = diff.GetArrayLength();

            // Delete obsolete items
            for (int i = list.Count; i > arrayLength; i--)
            {
                list.RemoveAt(i - 1);
            }

            // Update items
            for (int i = 0; i < Math.Min(list.Count, arrayLength); i++)
            {
                object item = list[i];
                JsonElement jsonItem = diff[i];
                if (jsonItem.ValueKind == JsonValueKind.Null)
                {
                    if (item != null)
                    {
                        list[i] = null;
                    }
                }
                else if (jsonItem.ValueKind == JsonValueKind.Array)
                {
                    IList subList = (IList)item;
                    PatchList(subList, subList.GetType().GetGenericArguments().Single(), jsonItem);
                }
                else if (jsonItem.ValueKind == JsonValueKind.Object)
                {
                    if (jsonItem.GetRawText() != "{}" || item == null)
                    {
                        object toPatch;
                        if (item == null)
                        {
                            // FIXME: JsonSerializer does not populate readonly (ObservableCollection) properties yet
                            toPatch = JsonSerializer.Deserialize(jsonItem.GetRawText(), itemType, JsonHelper.DefaultJsonOptions);
                        }
                        else
                        {
                            toPatch = item;
                        }
                        PatchObject(toPatch, jsonItem);

                        if (item == null)
                        {
                            list[i] = toPatch;
                        }
                    }
                }
                else
                {
                    object newValue = JsonSerializer.Deserialize(jsonItem.GetRawText(), itemType, JsonHelper.DefaultJsonOptions);
                    if (item != newValue)
                    {
                        list[i] = newValue;
                    }
                }
            }

            // Add missing items
            for (int i = list.Count; i < arrayLength; i++)
            {
                object newItem = JsonSerializer.Deserialize(diff[i].GetRawText(), itemType, JsonHelper.DefaultJsonOptions);
                list.Add(newItem);
            }
        }
    }
}
