using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Enumeration of supported plugin permissions
    /// </summary>
    [JsonConverter(typeof(SbcPermissionsConverter))]
    [Flags]
    public enum SbcPermissions
    {
        /// <summary>
        /// No permissions set (default value)
        /// </summary>
        None = 0,

        /// <summary>
        /// Execute generic commands
        /// </summary>
        CommandExecution = 1,

        /// <summary>
        /// Intercept codes but don't interact with them
        /// </summary>
        CodeInterceptionRead = 2,

        /// <summary>
        /// Intercept codes in a blocking way with options to resolve or cancel them
        /// </summary>
        CodeInterceptionReadWrite = 4,

        /// <summary>
        /// Install, load, unload, and uninstall plugins. Grants FS access to all third-party plugins too
        /// </summary>
        ManagePlugins = 8,

        /// <summary>
        /// Service plugin runtime information (for internal purposes only, do not use)
        /// </summary>
        ServicePlugins = 16,

        /// <summary>
        /// Manage user sessions
        /// </summary>
        ManageUserSessions = 32,

        /// <summary>
        /// Read from the object model
        /// </summary>
        ObjectModelRead = 64,

        /// <summary>
        /// Read from and write to the object model
        /// </summary>
        ObjectModelReadWrite = 128,

        /// <summary>
        /// Create new HTTP endpoints
        /// </summary>
        RegisterHttpEndpoints = 256,

        #region OS permissions enforced by AppArmor
        /// <summary>
        /// Read files in 0:/filaments
        /// </summary>
        ReadFilaments = 512,

        /// <summary>
        /// Write files in 0:/filaments
        /// </summary>
        WriteFilaments = 1024,

        /// <summary>
        /// Read files in 0:/firmware
        /// </summary>
        ReadFirmware = 2048,

        /// <summary>
        /// Write files in 0:/firmware
        /// </summary>
        WriteFirmware = 4096,

        /// <summary>
        /// Read files in 0:/gcodes
        /// </summary>
        ReadGCodes = 8192,

        /// <summary>
        /// Write files in 0:/gcodes
        /// </summary>
        WriteGCodes = 16384,

        /// <summary>
        /// Read files in 0:/macros
        /// </summary>
        ReadMacros = 32768,

        /// <summary>
        /// Write files in 0:/macros
        /// </summary>
        WriteMacros = 65536,

        /// <summary>
        /// Read files in 0:/menu
        /// </summary>
        ReadMenu = 131072,

        /// <summary>
        /// Write files in 0:/menu
        /// </summary>
        WriteMenu = 262144,

        /// <summary>
        /// Read files in 0:/sys
        /// </summary>
        ReadSystem = 524288,

        /// <summary>
        /// Write files in 0:/sys
        /// </summary>
        WriteSystem = 1048576,

        /// <summary>
        /// Read files in 0:/www
        /// </summary>
        ReadWeb = 2097152,

        /// <summary>
        /// Write files in 0:/www
        /// </summary>
        WriteWeb = 4194304,

        /// <summary>
        /// Access files including all subdirecotires of the virtual SD directory as DSF user
        /// </summary>
        FileSystemAccess = 8388608,

        /// <summary>
        /// Launch new processes
        /// </summary>
        LaunchProcesses = 16777216,

        /// <summary>
        /// Communicate over the network (stand-alone)
        /// </summary>
        NetworkAccess = 33554432,

        /// <summary>
        /// Access /dev/video* devices
        /// </summary>
        WebcamAccess = 134217728,

        /// <summary>
        /// Launch process as root user (for full device control - potentially dangerous)
        /// </summary>
        SuperUser = 67108864
        #endregion
    }

    /// <summary>
    /// Class to (de-)serialize SBC permission flags
    /// </summary>
    public class SbcPermissionsConverter : JsonConverter<SbcPermissions>
    {
        /// <summary>
        /// Checks if the given type can be converted
        /// </summary>
        /// <param name="typeToConvert">Type to convert</param>
        /// <returns>Whether the type can be converted</returns>
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(SbcPermissions);

        /// <summary>
        /// Read SBC permissions from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Target type</param>
        /// <param name="options">Serializer options</param>
        /// <returns>Deserialized permissions</returns>
        public override SbcPermissions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            SbcPermissions result = SbcPermissions.None;
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    SbcPermissions readValue = (SbcPermissions)Enum.Parse(typeof(SbcPermissions), reader.GetString(), true);
                    result |= readValue;
                }
            }
            return result;
        }

        /// <summary>
        /// Write SBC permissions to JSON
        /// </summary>
        /// <param name="writer">JSON writer</param>
        /// <param name="value">Value</param>
        /// <param name="options">Serializer options</param>
        public override void Write(Utf8JsonWriter writer, SbcPermissions value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (SbcPermissions permission in Enum.GetValues(typeof(SbcPermissions)))
            {
                if (permission != SbcPermissions.None && value.HasFlag(permission))
                {
                    string permissionString = JsonNamingPolicy.CamelCase.ConvertName(permission.ToString());
                    writer.WriteStringValue(permissionString);
                }
            }
            writer.WriteEndArray();
        }
    }
}
