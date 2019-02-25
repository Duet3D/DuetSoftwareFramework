using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace DuetAPI
{
    /// <summary>
    /// Helper class for
    /// - JSON serialization and deserialization
    /// - JSON patch creation and application
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Default JSON settings for serialization and deserialization.
        /// It is strongly recommended to use these settings for Newtonsoft.Json!
        /// </summary>
        public static JsonSerializerSettings DefaultSettings => new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        /// <summary>
        /// Create a JSON patch
        /// </summary>
        /// <param name="from">The source object</param>
        /// <param name="to">The updated object</param>
        /// <returns>The JSON patch as a JObject</returns>
        /// <seealso cref="PatchObject"/>
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
        /// <param name="obj">The object to patch</param>
        /// <param name="json">The generated JSON patch</param>
        /// <seealso cref="DiffObject"/>
        public static void PatchObject(object obj, JObject json)
        {
            Type type = obj.GetType();
            foreach (var pair in json)
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
                else if (token.Type == JTokenType.Object && list[i] != null)
                {
                    PatchObject(list[i], (JObject)token);
                }
                else if (token.Type == JTokenType.Array && list[i] != null && IsListType(list[i].GetType()))
                {
                    PatchList((IList)list[i], list[i].GetType(), (JArray)token);
                }
                else
                {
                    list[i] = token.ToObject(itemType);
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
        public override bool CanConvert(Type objectType)
        {
            return objectType.IsEnum;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            char asChar = (char)(int)value;
            writer.WriteValue(asChar.ToString());
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string value = (string)reader.Value;
            return (value?.Length == 1) ? (int)value[0] : existingValue;
        }
    }
}
