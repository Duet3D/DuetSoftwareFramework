using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DuetAPI
{
    public class JsonObject
    {
        [JsonIgnore]
        public JObject AsJson
        {
            get => JObject.FromObject(this, internalSerializer);
            set => Patch(value);
        }

        public JObject Diff(object to)
        {
            JObject toObj = (to != null) ? JObject.FromObject(to, internalSerializer) : null;
            return DiffObject(AsJson, toObj);
        }

        public void Patch(JObject diff) => PatchObject(this, diff);

        private static JsonSerializer internalSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = new List<JsonConverter> {
                new EnumCharConverter()
            }
        });

        private static bool HasValue(JToken item) => item != null && item.Type != JTokenType.Null;

        private static JObject DiffObject(JObject from, JObject to)
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

        private static bool IsListType(Type type) => typeof(IList).IsAssignableFrom(type) && type.GetGenericArguments().Length == 1;

        private static void PatchObject(object obj, JObject json)
        {
            foreach (var pair in json)
            {
                PropertyInfo prop = obj.GetType().GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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
    }

    public class EnumCharConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType.IsEnum;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            char asChar = (char)(int)value;
            writer.WriteValue(char.IsLetterOrDigit(asChar) ? (int)asChar : asChar);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string value = reader.ReadAsString();
            return (value?.Length == 1) ? reader.ReadAsString()[0] : Activator.CreateInstance(objectType);
        }
    }
}
