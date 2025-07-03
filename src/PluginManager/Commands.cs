using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPIClient;

namespace PluginManager;

public static class Commands
{
    /// <summary>
    /// List all installed plugins
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <returns>Exit code</returns>
    public static async Task<int> ListAsync(FileInfo socketPath, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Get the object model
        var model = await connection.GetObjectModelAsync(cancellationToken);
        if (model.Plugins.Count > 0)
        {
            Console.WriteLine("{0,-24} {1,-16} {2,-16} {3,-24} {4,-24} {5,-12}", "Plugin", "Id", "Version", "Author", "License", "Status");
            foreach (var item in model.Plugins.Values)
            {
                if (item is not null)
                {
                    string pluginState = "n/a";
                    if (!string.IsNullOrEmpty(item.SbcExecutable))
                    {
                        pluginState = (item.Pid > 0) ? "Started" : "Stopped";
                    }
                    Console.WriteLine("{0,-24} {1,-16} {2,-16} {3,-24} {4,-24} {5,-12}", item.Name, item.Id, item.Version, item.Author, item.License, pluginState);
                }
            }
        }
        else
        {
            Console.WriteLine("No plugins installed");
        }

        // Done
        return 0;
    }

    /// <summary>
    /// List all data of installed plugins
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> ListDataAsync(FileInfo socketPath, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Get the object model
        var model = await connection.GetObjectModelAsync(cancellationToken);
        if (model.Plugins.Count > 0)
        {
            foreach (var item in model.Plugins.Values)
            {
                Console.WriteLine("Plugin {0}:", item.Id);
                foreach (var kv in item.Data)
                {
                    Console.WriteLine("{0} = {1}", kv.Key, JsonSerializer.Serialize(kv.Value, DuetAPI.Utility.JsonHelper.DefaultJsonOptions));
                }
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("No plugins installed");
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Install a plugin
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable message output</param>
    /// <param name="zipFile">ZIP file to install</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="NotImplementedException"></exception>
    public static async Task InstallAsync(FileInfo socketPath, bool quiet, FileInfo zipFile, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return;
        }

        // Install the plugin
        try
        {
            await connection.InstallPluginAsync(zipFile.FullName, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("Plugin installed");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to install plugin: {0}", e.Message);
        }
    }

    /// <summary>
    /// Reload a plugin manifest
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable message output</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> ReloadAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Install the plugin
        try
        {
            await connection.ReloadPluginAsync(id, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("Plugin manifest reloaded");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to reload plugin: {0}", e.Message);
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Start a plugin
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable message output</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> StartAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Start the plugin
        try
        {
            await connection.StartPluginAsync(id, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("Plugin started");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to start plugin: {0}", e.Message);
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Set data of a plugin
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable message output</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="key">Key</param>
    /// <param name="value">Value</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> SetDataAsync(FileInfo socketPath, bool quiet, string id, string key, string value, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Try to set the data
        try
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(value);
                await connection.SetPluginDataAsync(key, json.RootElement, id, cancellationToken);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("Invalid JSON data");
            }

            if (!quiet)
            {
                Console.WriteLine("Plugin data set");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to set plugin data: {0}", e.Message);
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Stop a plugin
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable message output</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> StopAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Stop the plugin
        try
        {
            await connection.StopPluginAsync(id, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("Plugin stopped");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to stop plugin: {0}", e.Message);
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Uninstall a plugin
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable output messages</param>
    /// <param name="id">Plugin ID</param>
    /// <returns>Exit code</returns>
    public static async Task<int> UninstallAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Uninstall the plugin
        try
        {
            await connection.UninstallPluginAsync(id, cancellationToken);
            if (!quiet)
            {
                Console.WriteLine("Plugin uninstalled");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to uninstall plugin: {0}", e.Message);
        }

        // Done
        return 0;
    }

    /// <summary>
    /// Check if a plugin is installed
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable output messages</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> IsInstalledAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Check if the plugin is installed
        var model = await connection.GetObjectModelAsync(cancellationToken);
        bool isInstalled = model.Plugins.ContainsKey(id);
        if (!quiet)
        {
            if (isInstalled)
            {
                Console.WriteLine("Plugin is installed");
            }
            else
            {
                Console.WriteLine("Plugin is not installed");
            }
        }
        return isInstalled ? 0 : 1;
    }

    /// <summary>
    /// Check if a plugin is started
    /// </summary>
    /// <param name="socketPath">UNIX socket path</param>
    /// <param name="quiet">Disable output messages</param>
    /// <param name="id">Plugin ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exit code</returns>
    public static async Task<int> IsStartedAsync(FileInfo socketPath, bool quiet, string id, CancellationToken cancellationToken)
    {
        // Connect to DCS
        using CommandConnection connection = new();
        try
        {
            await connection.ConnectAsync(socketPath.FullName, cancellationToken);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Failed to connect to DCS: {0}", e.Message);
            return 1;
        }

        // Check if the plugin is started
        var model = await connection.GetObjectModelAsync(cancellationToken);
        if (model.Plugins.TryGetValue(id, out Plugin pluginItem) && pluginItem.Pid > 0)
        {
            if (!quiet)
            {
                Console.WriteLine("Plugin is started");
            }
            return 0;
        }
        if (!quiet)
        {
            Console.WriteLine("Plugin is not started");
        }
        return 1;
    }
}
