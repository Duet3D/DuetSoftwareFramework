using System.Threading.Tasks;
using DuetAPIClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace DuetWebServer.FileProviders
{
    /// <summary>
    /// Static file provider that uses DCS to resolve file paths
    /// </summary>
    public class DuetFileProvider : IFileProvider
    {
        private string _wwwRoot;
        private PhysicalFileProvider _provider;
        
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Creates a new file resolver instance
        /// </summary>
        /// <param name="configuration">Configuration provider</param>
        public DuetFileProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }
        
        /// <summary>
        /// Gets the file info of the specified path
        /// </summary>
        /// <param name="subpath">Target path</param>
        /// <returns>File info</returns>
        public IFileInfo GetFileInfo(string subpath)
        {
            ValidateProvider().Wait();
            return _provider.GetFileInfo(subpath);
        }

        /// <summary>
        /// Returns the contents of the given directory
        /// </summary>
        /// <param name="subpath">Target path</param>
        /// <returns>Directory contents</returns>
        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            ValidateProvider().Wait();
            return _provider.GetDirectoryContents(subpath);
        }

        /// <summary>
        /// Creates a token that watches for changes
        /// </summary>
        /// <param name="filter">Watch filter</param>
        /// <returns>Change token</returns>
        public IChangeToken Watch(string filter)
        {
            ValidateProvider().Wait();
            return _provider.Watch(filter);
        }

        private async Task ValidateProvider()
        {
            using (CommandConnection connection = new CommandConnection())
            {
                await connection.Connect(_configuration.GetValue("SocketPath", DuetAPI.Connection.Defaults.SocketPath));
                string wwwRoot = await connection.ResolvePath("0:/www");
                if (wwwRoot != _wwwRoot)
                {
                    _provider = new PhysicalFileProvider(wwwRoot);
                    _wwwRoot = wwwRoot;
                }
            }
        }
    }
}