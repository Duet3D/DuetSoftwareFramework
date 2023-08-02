using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.RemoveUserSession"/> command
    /// </summary>
    public sealed class RemoveUserSession : DuetAPI.Commands.RemoveUserSession
    {
        /// <summary>
        /// Remove an existing user session
        /// </summary>
        /// <returns>True if the user session could be removed</returns>
        public override async Task<bool> Execute()
        {
            using (await Model.Provider.AccessReadWriteAsync())
            {
                for (int i = 0; i < Model.Provider.Get.SBC!.DSF.UserSessions.Count; i++)
                {
                    if (Model.Provider.Get.SBC!.DSF.UserSessions[i].Id == Id)
                    {
                        Model.Provider.Get.SBC!.DSF.UserSessions.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
