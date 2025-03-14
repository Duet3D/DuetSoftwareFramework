using System.Threading;
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
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the user session could be removed</returns>
        public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using (await Model.Provider.AccessReadWriteAsync(cancellationToken))
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
