using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Wait for the machine model to be up-to-date. DuetControlServer maintains it in process, so this
/// returns at once and is kept for the clients that still ask
/// </summary>
/// TODO remove this unused command
[RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
public partial class SyncObjectModel : Command { }
