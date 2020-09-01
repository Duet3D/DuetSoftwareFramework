using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Query the current object model
    /// </summary>
    /// <seealso cref="ObjectModel.ObjectModel"/>
    [RequiredPermissions(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
    public class GetObjectModel : Command<ObjectModel.ObjectModel> { }
}