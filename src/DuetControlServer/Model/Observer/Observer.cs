using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace DuetControlServer.Model;

/// <summary>
/// Main class for observing changes in the machine model
/// </summary>
/// <param name="model">Object model</param>
public partial class Observer(ObjectModel model) : IHostedService
{
    /// <summary>
    /// Delegate to call when a property is being changed
    /// </summary>
    /// <param name="path">Path to the value that changed</param>
    /// <param name="changeType">Type of the modification</param>
    /// <param name="value">New value</param>
    public delegate void PropertyPathChanged(object[] path, PropertyChangeType changeType, object? value);

    /// <summary>
    /// Event to call when an object model value has been changed
    /// </summary>
    public event PropertyPathChanged? OnPropertyPathChanged;

    /// <summary>
    /// Add a new element to a property path
    /// </summary>
    /// <param name="path">Existing path</param>
    /// <param name="toAdd">Element(s) to add</param>
    /// <returns>Combined property path</returns>
    private static object[] AddToPath(object[] path, params object[] toAdd)
    {
        object[] newPath = new object[path.Length + toAdd.Length];
        path.CopyTo(newPath, 0);
        toAdd.CopyTo(newPath, path.Length);
        return newPath;
    }

    /// <summary>
    /// Starts the observer service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeToModelObject(model, []);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the observer service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
