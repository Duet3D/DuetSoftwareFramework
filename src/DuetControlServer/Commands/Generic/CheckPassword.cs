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
        /// <returns>Asynchronous task</returns>
        public override async Task<bool> Execute()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Password == Model.Provider.DefaultPassword || string.IsNullOrEmpty(Model.Provider.Password))
                {
                    // No password set
                    return true;
                }
                return (Password == Model.Provider.Password);
            }
        }
    }
}
