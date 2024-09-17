using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Context for JSON handling of IPC commands
    /// </summary>
    [JsonSerializable(typeof(Code))]
    [JsonSerializable(typeof(DuetAPI.Commands.Cancel))]
    [JsonSerializable(typeof(DuetAPI.Commands.Ignore))]
    [JsonSerializable(typeof(DuetAPI.Commands.Resolve))]
    [JsonSerializable(typeof(GetFileInfo))]
    [JsonSerializable(typeof(ResolvePath))]
    [JsonSerializable(typeof(CheckPassword))]
    [JsonSerializable(typeof(EvaluateExpression))]
    [JsonSerializable(typeof(Flush))]
    [JsonSerializable(typeof(InvalidateChannel))]
    [JsonSerializable(typeof(SetUpdateStatus))]
    [JsonSerializable(typeof(SimpleCode))]
    [JsonSerializable(typeof(WriteMessage))]
    [JsonSerializable(typeof(DuetAPI.Commands.SendHttpResponse))]
    [JsonSerializable(typeof(AddHttpEndpoint))]
    [JsonSerializable(typeof(DuetAPI.Commands.ReceivedHttpRequest))]
    [JsonSerializable(typeof(RemoveHttpEndpoint))]
    [JsonSerializable(typeof(DuetAPI.Commands.Acknowledge))]
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
    [JsonSerializable(typeof(DuetAPI.Commands.BaseResponse))]
    [JsonSerializable(typeof(DuetAPI.Commands.ErrorResponse))]
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