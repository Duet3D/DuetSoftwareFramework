namespace DuetAPI.Commands;

/// <summary>
/// May be used by third-party plugins to flag when they have fully started
/// </summary>
/// <remarks>
/// In order to use this command, the plugin manifest must have the
/// <see cref="ObjectModel.PluginManifest.SbcNotifyStarted"/> property set.
/// This can be useful if custom codes are used in dsf-config.g, because this
/// can guarantee that the necessary code interceptors are active before it
/// is started.
/// </remarks>
public partial class NotifyPluginStarted : Command
{
    /// <summary>
    /// Identifier of the plugin (only mandatory if running as root)
    /// </summary>
    public string? Plugin { get; set; }
}
