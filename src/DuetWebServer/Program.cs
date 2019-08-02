using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DuetWebServer
{
    /// <summary>
    /// Main class of the ASP.NET Core endpoint
    /// </summary>
    public class Program
    {
        private const string DefaultConfigFile = "/opt/dsf/conf/http.json";
        
        /// <summary>
        /// Called when the application is launched
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// Creates a new WebHost instance
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        /// <returns>Web host builder</returns>
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile(DefaultConfigFile, false, true);
                    config.AddCommandLine(args);
                })
                .UseStartup<Startup>();
    }
}
