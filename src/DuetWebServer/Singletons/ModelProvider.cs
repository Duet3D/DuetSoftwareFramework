using DuetAPI.ObjectModel;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace DuetWebServer.Singletons
{
    /// <summary>
    /// Interface for accessing model-related parameters
    /// </summary>
    public interface IModelProvider
    {
        /// <summary>
        /// Dictionary of registered third-party paths vs third-party HTTP endpoints
        /// </summary>
        public Dictionary<string, HttpEndpoint> Endpoints { get; }

        /// <summary>
        /// Sequence number for generic messages (analog to seqs.reply)
        /// </summary>
        public int ReplySeq { get; set; }

        /// Sequence number for volume changes (analog to seqs.volumes)
        public int VolumesSeq { get; set; }

        /// <summary>
        /// Path to the web directory
        /// </summary>
        public string? WebDirectory { get; set; }

        /// <summary>
        /// Delegate for an event that is triggered when the path of the web directory changes
        /// </summary>
        /// <param name="webDirectory">New web directory</param>
        public delegate void WebDirectoryChanged(string webDirectory);

        /// <summary>
        /// Event that is triggered whenever the web directory path changes
        /// </summary>
        public event WebDirectoryChanged? OnWebDirectoryChanged;
    }

    /// <summary>
    /// Singleton providing model-related parameters
    /// </summary>
    public class ModelProvider : IModelProvider
    {
        /// <summary>
        /// Dictionary of registered third-party paths vs third-party HTTP endpoints
        /// </summary>
        public Dictionary<string, HttpEndpoint> Endpoints { get; } = new();

        /// <summary>
        /// Sequence number for generic messages (analog to seqs.reply)
        /// </summary>
        public int ReplySeq { get; set; }

        /// Sequence number for volume changes (analog to seqs.volumes)
        public int VolumesSeq { get; set; }

        /// <summary>
        /// Path to the web directory
        /// </summary>
        public string? WebDirectory
        {
            get => _webDirectory;
            set
            {
                if (value != _webDirectory)
                {
                    _webDirectory = value;
                    OnWebDirectoryChanged?.Invoke(value!);
                }
            }

        }
        private string? _webDirectory;

        /// <summary>
        /// Event that is triggered whenever the web directory path changes
        /// </summary>
        public event IModelProvider.WebDirectoryChanged? OnWebDirectoryChanged;

        /// <summary>
        /// Constructor of this singleton
        /// </summary>
        /// <param name="configuration">Configuration provider</param>
        public ModelProvider(IConfiguration configuration) => WebDirectory = configuration.Get<Settings>().DefaultWebDirectory;
    }
}
