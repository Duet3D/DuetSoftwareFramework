using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DuetRestEndpoint
{
    /// <summary>
    /// Main class of the ASP.NET Core endpoint
    /// </summary>
    public class Program
    {
        private static readonly string DefaultConfigFile = "/etc/duet/http.json";
        
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
        /// <returns></returns>
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile(DefaultConfigFile, true, false);
                    config.AddCommandLine(args);
                })
                .UseStartup<Startup>();
    }
}
