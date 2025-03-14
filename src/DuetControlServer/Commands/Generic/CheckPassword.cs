using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.CheckPassword"/> command
    /// </summary>
    public sealed class CheckPassword : DuetAPI.Commands.CheckPassword
    {
        /// <summary>
        /// Check the given password
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public override async Task<bool> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Password == DuetAPI.Connection.Defaults.Password || string.IsNullOrEmpty(Model.Provider.Password))
                {
                    // No password set
                    return true;
                }
                return (Password == Model.Provider.Password);
            }
        }
    }
}
