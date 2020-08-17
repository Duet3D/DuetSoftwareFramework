using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Enumeration of supported plugin permissions
    /// </summary>
	[JsonConverter(typeof(SbcPermissionsConverter))]
    public enum SbcPermissions
    {
		/// <summary>
		/// No permissions set (default value)
		/// </summary>
		None,

		/// <summary>
		/// Execute generic commands
		/// </summary>
		CommandExecution,

		/// <summary>
		/// Intercept codes in a non-blocking way
		/// </summary>
		CodeInterceptionRead,

		/// <summary>
		/// Intercept codes in a blocking way with options to resolve or cancel them
		/// </summary>
		CodeInterceptionReadWrite,

		/// <summary>
		/// Install, load, unload, and uninstall plugins
		/// </summary>
		ManagePlugins,

		/// <summary>
		/// Manage user sessions
		/// </summary>
		ManageUserSessions,

		/// <summary>
		/// Read from the object model
		/// </summary>
		ObjectModelRead,

		/// <summary>
		/// Read from and write to the object model
		/// </summary>
		ObjectModelReadWrite,

		/// <summary>
		/// Create new HTTP endpoints
		/// </summary>
		RegisterHttpEndpoints,

        #region Reserved permissions - will require AppArmor and elevation process
        /// <summary>
        /// Read files in 0:/filaments
        /// </summary>
        ReadFilaments,

        /// <summary>
        /// Write files in 0:/filaments
        /// </summary>
        WriteFilaments,

		/// <summary>
		/// Read files in 0:/firmware
		/// </summary>
		ReadFirmware,

		/// <summary>
		/// Write files in 0:/firmware
		/// </summary>
		WriteFirmware,

		/// <summary>
		/// Read files in 0:/gcodes
		/// </summary>
		ReadGCodes,

		/// <summary>
		/// Write files in 0:/gcodes
		/// </summary>
		WriteGCodes,

		/// <summary>
		/// Read files in 0:/macros
		/// </summary>
		ReadMacros,

		/// <summary>
		/// Write files in 0:/macros
		/// </summary>
		WriteMacros,

		/// <summary>
		/// Read files in 0:/sys
		/// </summary>
		ReadSystem,

		/// <summary>
		/// Write files in 0:/sys
		/// </summary>
		WriteSystem,

		/// <summary>
		/// Read files in 0:/www
		/// </summary>
		ReadWeb,

		/// <summary>
		/// Write files in 0:/www
		/// </summary>
		WriteWeb,

		/// <summary>
		/// Access files outside the virtual SD directory (as DSF user)
		/// </summary>
		FileSystemAccess,

        /// <summary>
        /// Launch new processes
        /// </summary>
        LaunchProcesses,

		/// <summary>
		/// Communicate over the network (stand-alone)
		/// </summary>
		NetworkAccess,

        /// <summary>
        /// Launch process as root user (for full device control - potentially dangerous)
        /// </summary>
        SuperUser
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
			foreach (Enum permission in Enum.GetValues(typeof(SbcPermissions)))
            {
				if (value.HasFlag(permission) && !permission.Equals(SbcPermissions.None))
                {
					string permissionString = JsonNamingPolicy.CamelCase.ConvertName(permission.ToString());
					writer.WriteStringValue(permissionString);
                }
            }
			writer.WriteEndArray();
        }
    }
}
