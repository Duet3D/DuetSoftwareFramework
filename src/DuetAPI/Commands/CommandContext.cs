﻿using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Context for JSON handling of IPC commands
    /// </summary>
    // Supported command types
    [JsonSerializable(typeof(Code))]
    [JsonSerializable(typeof(Cancel))]
    [JsonSerializable(typeof(Ignore))]
    [JsonSerializable(typeof(Resolve))]
    [JsonSerializable(typeof(GetFileInfo))]
    [JsonSerializable(typeof(ResolvePath))]
    [JsonSerializable(typeof(CheckPassword))]
    [JsonSerializable(typeof(EvaluateExpression))]
    [JsonSerializable(typeof(Flush))]
    [JsonSerializable(typeof(InvalidateChannel))]
    [JsonSerializable(typeof(SetUpdateStatus))]
    [JsonSerializable(typeof(SimpleCode))]
    [JsonSerializable(typeof(WriteMessage))]
    [JsonSerializable(typeof(SendHttpResponse))]
    [JsonSerializable(typeof(AddHttpEndpoint))]
    [JsonSerializable(typeof(ReceivedHttpRequest))]
    [JsonSerializable(typeof(RemoveHttpEndpoint))]
    [JsonSerializable(typeof(Acknowledge))]
    [JsonSerializable(typeof(GetObjectModel))]
    [JsonSerializable(typeof(LockObjectModel))]
    [JsonSerializable(typeof(PatchObjectModel))]
    [JsonSerializable(typeof(SetNetworkProtocol))]
    [JsonSerializable(typeof(SetObjectModel))]
    [JsonSerializable(typeof(SyncObjectModel))]
    [JsonSerializable(typeof(UnlockObjectModel))]
    [JsonSerializable(typeof(InstallSystemPackage))]
    [JsonSerializable(typeof(UninstallSystemPackage))]
    [JsonSerializable(typeof(InstallPlugin))]
    [JsonSerializable(typeof(ReloadPlugin))]
    [JsonSerializable(typeof(SetPluginData))]
    [JsonSerializable(typeof(SetPluginProcess))]
    [JsonSerializable(typeof(StartPlugin))]
    [JsonSerializable(typeof(StartPlugins))]
    [JsonSerializable(typeof(StopPlugin))]
    [JsonSerializable(typeof(StopPlugins))]
    [JsonSerializable(typeof(UninstallPlugin))]
    [JsonSerializable(typeof(AddUserSession))]
    [JsonSerializable(typeof(RemoveUserSession))]
    // Generic responses
    [JsonSerializable(typeof(BaseResponse))]
    [JsonSerializable(typeof(ErrorResponse))]
    // Specific command results
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSourceGenerationOptions(PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    public sealed partial class CommandContext : JsonSerializerContext
    {
        static CommandContext() => Default = new CommandContext(CreateJsonSerializerOptions(Default));

        private static JsonSerializerOptions CreateJsonSerializerOptions(CommandContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}