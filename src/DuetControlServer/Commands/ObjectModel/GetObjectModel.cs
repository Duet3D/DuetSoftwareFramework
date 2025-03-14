using DuetAPI.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Command"/> command
    /// </summary>
    public sealed class GetObjectModel : DuetAPI.Commands.GetObjectModel
    {
        /// <summary>
        /// Retrieve a copy of the current machine model
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Clone of the current machine model</returns>
        public override async Task<ObjectModel> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using (await Model.Provider.AccessReadOnlyAsync(cancellationToken))
            {
                return (ObjectModel)Model.Provider.Get.Clone();
            }
        }
    }
}