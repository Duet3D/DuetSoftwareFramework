using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DuetAPI
{
    /// <summary>
    /// Helper class for JSON serialization, deserialization, patch creation and patch application
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Default JSON settings for serialization and deserialization.
        /// It is strongly recommended to use these settings with Newtonsoft.Json!
        /// </summary>
        public static readonly JsonSerializerSettings DefaultSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        /// <summary>
        /// Default JSON serializer.
        /// It is strongly recommended to use this serializer with Newtonsoft.Json!
        /// </summary>
        public static readonly JsonSerializer DefaultSerializer = new JsonSerializer
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        /// <summary>
        /// Create a JSON patch
        /// </summary>
        /// <param name="from">Source object</param>
        /// <param name="to">Updated object</param>
        /// <returns>JSON patch</returns>
        /// <seealso cref="PatchObject(JObject, JObject)"/>
        public static JObject DiffObject(JObject from, JObject to)
        {
            if (HasValue(from) != HasValue(to))
            {
                return to;
            }

            JObject diff = new JObject();
            foreach (var pair in from)
            {
                string key = char.ToLowerInvariant(pair.Key[0]) + pair.Key.Substring(1);
                if (to.TryGetValue(key, StringComparison.InvariantCultureIgnoreCase, out JToken value))
                {
                    if (value.Type == JTokenType.Object)
                    {
                        JToken subDiff = DiffObject((JObject)pair.Value, (JObject)value);
                        if (subDiff.HasValues)
                        {
                            diff[key] = subDiff;
                        }
                    }
                    else if (value.Type == JTokenType.Array)
                    {
                        JArray subDiff = DiffArray((JArray)pair.Value, (JArray)value, out bool foundDiffs);
                        if (foundDiffs)
                        {
                            diff[key] = subDiff;
                        }
                    }
                    else if (!JToken.DeepEquals(pair.Value, value))
                    {
                        diff[key] = value;
                    }
                }
            }
            foreach (var pair in to)
            {
                string key = char.ToLowerInvariant(pair.Key[0]) + pair.Key.Substring(1);
                if (!to.ContainsKey(key))
                {
                    diff[key] = pair.Value;
                }
            }
            return diff;
        }

        private static JArray DiffArray(JArray from, JArray to, out bool foundDiffs)
        {
            if (HasValue(from) != HasValue(to))
            {
                foundDiffs = true;
                return to;
            }

            JArray diff = new JArray();
            foundDiffs = (from.Count != to.Count);
            for (int i = 0; i < Math.Min(from.Count, to.Count); i++)
            {
                if (HasValue(from[i]) != HasValue(to[i]))
                {
                    foundDiffs = true;
                    diff.Add(to[i]);
                }
                else if (from[i].Type == JTokenType.Object)
                {
                    JObject diffObj = DiffObject((JObject)from[i], (JObject)to[i]);
                    foundDiffs |= diffObj.HasValues;
                    diff.Add(diffObj);
                }
                else if (from[i].Type == JTokenType.Array)
                {
                    JArray diffArr = DiffArray((JArray)from[i], (JArray)to[i], out bool foundSubDiffs);
                    foundDiffs |= foundSubDiffs;
                    diff.Add(diffArr);
                }
                else
                {
                    foundDiffs |= !JToken.DeepEquals(from[i], to[i]);
                    diff.Add(to[i]);
                }
            }
            for (int i = from.Count; i < to.Count; i++)
            {
                diff.Add(to[i]);
            }
            return diff;
        }

        /// <summary>
        /// Apply an arbitrary JSON patch
        /// </summary>
        /// <param name="obj">Object to patch</param>
        /// <param name="diff">JSON patch to apply</param>
        public static void PatchObject(JObject obj, JObject diff)
        {
            foreach (var pair in diff)
            {
                JToken token = obj[pair.Key];
                if (token != null)
                {
                    if (token.Type == JTokenType.Array)
                    {
                        PatchList((JArray)token, (JArray)pair.Value);
                    }
                    else if (token.Type == JTokenType.Object)
                    {
                        PatchObject((JObject)token, (JObject)pair.Value);
                    }
                    else
                    {
                        obj[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    obj[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        /// Apply an arbitrary JSON patch
        /// </summary>
        /// <param name="obj">Object to patch</param>
        /// <param name="diff">JSON patch to apply</param>
        /// <seealso cref="DiffObject"/>
        public static void PatchObject(object obj, JObject diff)
        {
            Type type = obj.GetType();
            foreach (var pair in diff)
            {
                PropertyInfo prop = type.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    if (pair.Value.Type == JTokenType.Array && IsListType(prop.PropertyType))
                    {
                        JArray subArray = (JArray)pair.Value;
                        IList list = (IList)prop.GetValue(obj);
                        PatchList(list, prop.PropertyType.GetGenericArguments().Single(), subArray);
                    }
                    else if (pair.Value.Type == JTokenType.Object)
                    {
                        object subObj = prop.GetValue(obj);
                        PatchObject(subObj, (JObject)pair.Value);
                    }
                    else if (prop.PropertyType.IsEnum && pair.Value?.ToString().Length == 1)
                    {
                        prop.SetValue(obj, pair.Value.ToString()[0]);
                    }
                    else
                    {
                        prop.SetValue(obj, pair.Value.ToObject(prop.PropertyType));
                    }
                }
            }
        }

        private static void PatchList(JArray a, JArray b)
        {
            for (int i = a.Count - 1; i >= b.Count; i--)
            {
                a.RemoveAt(i);
            }

            for (int i = 0; i < b.Count; i++)
            {
                JToken token = b[i];
                if (i >= a.Count)
                {
                    a.Add(token);
                }
                else
                {
                    JToken source = a[i];
                    if (HasValue(source) && token.Type == JTokenType.Object)
                    {
                        PatchObject((JObject)source, (JObject)token);
                    }
                    else if (HasValue(source) && token.Type == JTokenType.Array)
                    {
                        PatchList((JArray)source, (JArray)token);
                    }
                    else
                    {
                        a[i] = b[i];
                    }
                }
            }
        }

        private static void PatchList(IList list, Type itemType, JArray array)
        {
            for (int i = list.Count - 1; i >= array.Count; i--)
            {
                list.RemoveAt(i);
            }

            for (int i = 0; i < array.Count; i++)
            {
                JToken token = array[i];
                if (i >= list.Count)
                {
                    list.Add(token.ToObject(itemType));
                }
                else
                {
                    object source = list[i];
                    if (source != null && token.Type == JTokenType.Object && list[i] != null)
                    {
                        PatchObject(source, (JObject)token);
                    }
                    else if (source != null && token.Type == JTokenType.Array && IsListType(list[i].GetType()))
                    {
                        PatchList((IList)source, list[i].GetType(), (JArray)token);
                    }
                    else
                    {
                        list[i] = token.ToObject(itemType);
                    }
                }
            }
        }
        
        private static bool HasValue(JToken item) => item != null && item.Type != JTokenType.Null;

        private static bool IsListType(Type type) => typeof(IList).IsAssignableFrom(type) && type.GetGenericArguments().Length == 1;
    }
    
    /// <summary>
    /// Helper class for Newtonsoft.Json to convert char enums to strings and vice versa
    /// </summary>
    public class CharEnumConverter : JsonConverter
    {
        /// <summary>
        /// Checks if the object can be converted
        /// </summary>
        /// <param name="objectType">Object type to check</param>
        /// <returns>Whether the object can be converted</returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType.IsEnum;
        }

        /// <summary>
        /// Writes a char enum to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="serializer">JSON Serializer</param>
        /// <param name="value">Value to write</param>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            char asChar = (char)(int)value;
            writer.WriteValue(asChar.ToString());
        }

        /// <summary>
        /// Reads a char enum from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="objectType">Object type</param>
        /// <param name="existingValue">Existing value</param>
        /// <param name="serializer">JSON serializer</param>
        /// <returns>Deserialized char enum</returns>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string value = (string)reader.Value;
            return (value?.Length == 1) ? (int)value[0] : existingValue;
        }
    }
}
