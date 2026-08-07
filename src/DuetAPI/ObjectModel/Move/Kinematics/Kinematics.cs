using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Information about the configured geometry
/// </summary>
[JsonDerivedType(typeof(CoreKinematics))]
[JsonDerivedType(typeof(DeltaKinematics))]
[JsonDerivedType(typeof(HangprinterKinematics))]
[JsonDerivedType(typeof(ScaraKinematics))]
[JsonDerivedType(typeof(PolarKinematics))]
public partial class Kinematics : ModelObject, IDynamicModelObject
{
    /// <summary>
    /// Name of the configured kinematics
    /// </summary>
    public KinematicsName Name
    {
        get => _name;
        protected set => SetPropertyValue(ref _name, value);
    }
    private KinematicsName _name = KinematicsName.Unknown;

    /// <summary>
    /// Segmentation parameters or null if not configured
    /// </summary>
    public MoveSegmentation? Segmentation
    {
        get => _segmentation;
        set => SetPropertyValue(ref _segmentation, value);
    }
    private MoveSegmentation? _segmentation;

    /// <summary>
    /// Update this instance from a given JSON element
    /// </summary>
    /// <param name="jsonElement">Element to update this intance from</param>
    /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
    /// <returns>Updated instance</returns>
    /// <exception cref="JsonException">Failed to deserialize data</exception>
    public IDynamicModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
    {
        if (jsonElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (jsonElement.TryGetProperty("name", out JsonElement nameProperty))
        {
            // Compare case-insensitively, the firmware reports some kinematics with capitalized names
            string? name = nameProperty.GetString()?.ToLowerInvariant();
            if (name is "cartesian" or "corexy" or "corexyu" or "corexyuv" or "corexz" or "markforged")
            {
                if (this is not CoreKinematics)
                {
                    Kinematics newKinematics = new CoreKinematics();
                    return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (name is "delta" or "lineardelta")
            {
                if (this is not DeltaKinematics)
                {
                    Kinematics newKinematics = new DeltaKinematics();
                    return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (name is "hangprinter")
            {
                if (this is not HangprinterKinematics)
                {
                    Kinematics newKinematics = new HangprinterKinematics();
                    return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (name is "fivebarscara" or "scara")
            {
                if (this is not ScaraKinematics)
                {
                    Kinematics newKinematics = new ScaraKinematics();
                    return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (name is "polar")
            {
                if (this is not PolarKinematics)
                {
                    Kinematics newKinematics = new PolarKinematics();
                    return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
                }
            }
            else if (this is CoreKinematics or DeltaKinematics or HangprinterKinematics or ScaraKinematics or PolarKinematics)
            {
                Kinematics newKinematics = new();
                return newKinematics.UpdateFromJson(jsonElement, ignoreSbcProperties);
            }
        }
        return GeneratedUpdateFromJson(jsonElement, ignoreSbcProperties);
    }

    /// <summary>
    /// Update this instance from a given JSON reader
    /// </summary>
    /// <param name="reader">JSON reader</param>
    /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
    /// <param name="scope">Extent of this update</param>
    /// <returns>Updated instance</returns>
    /// <exception cref="JsonException">Failed to deserialize data</exception>
    public IDynamicModelObject? UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties, ModelUpdateScope scope = ModelUpdateScope.Patch)
    {
        if (reader.TokenType == JsonTokenType.None && !reader.Read())
        {
            throw new JsonException("failed to read from JSON reader");
        }
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("expected start of object");
        }

        Utf8JsonReader readerCopy = reader;
        while (readerCopy.Read() && readerCopy.TokenType != JsonTokenType.EndObject)
        {
            if (readerCopy.TokenType == JsonTokenType.PropertyName)
            {
                if (readerCopy.ValueTextEquals("name"u8) && readerCopy.Read())
                {
                    // Compare case-insensitively, the firmware reports some kinematics with capitalized names
                    string? name = readerCopy.GetString()?.ToLowerInvariant();
                    if (name is "cartesian" or "corexy" or "corexyu" or "corexyuv" or "corexz" or "markforged")
                    {
                        if (this is not CoreKinematics)
                        {
                            Kinematics newKinematics = new CoreKinematics();
                            return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                        }
                    }
                    else if (name is "delta" or "lineardelta")
                    {
                        if (this is not DeltaKinematics)
                        {
                            Kinematics newKinematics = new DeltaKinematics();
                            return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                        }
                    }
                    else if (name is "hangprinter")
                    {
                        if (this is not HangprinterKinematics)
                        {
                            Kinematics newKinematics = new HangprinterKinematics();
                            return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                        }
                    }
                    else if (name is "fivebarscara" or "scara")
                    {
                        if (this is not ScaraKinematics)
                        {
                            Kinematics newKinematics = new ScaraKinematics();
                            return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                        }
                    }
                    else if (name is "polar")
                    {
                        if (this is not PolarKinematics)
                        {
                            Kinematics newKinematics = new PolarKinematics();
                            return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                        }
                    }
                    else if (this is CoreKinematics or DeltaKinematics or HangprinterKinematics or ScaraKinematics or PolarKinematics)
                    {
                        Kinematics newKinematics = new();
                        return newKinematics.UpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
                    }
                }
                else
                {
                    readerCopy.Skip();
                }
            }
            else if (readerCopy.TokenType == JsonTokenType.StartObject)
            {
                readerCopy.Skip();
            }
        }
        return GeneratedUpdateFromJsonReader(ref reader, ignoreSbcProperties, scope);
    }
}
