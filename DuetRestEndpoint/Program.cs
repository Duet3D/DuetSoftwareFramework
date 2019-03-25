using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;

namespace DuetRestEndpoint
{
    /// <summary>
    /// Main class of the ASP.NET Core endpoint
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Called when the application is launched
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// Creates a new WebHost
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        /// <returns></returns>
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseUrls("http://0.0.0.0:5000")
                .UseStartup<Startup>();
    }
}
