using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DuetWebServer
{
    /// <summary>
    /// Main class of the ASP.NET Core endpoint
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Default path to the configuration file
        /// </summary>
        public const string DefaultConfigFile = "/opt/dsf/conf/http.json";
        
        /// <summary>
        /// Called when the application is launched
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        /// <summary>
        /// Creates a new WebHost instance
        /// </summary>
        /// <param name="args">Command-line arguments</param>
        /// <returns>Web host builder</returns>
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.AddJsonFile(DefaultConfigFile, false, true);
                    config.AddCommandLine(args);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(typeof(IModelProvider), typeof(ModelProvider));
                    services.AddSingleton(typeof(ISessionStorage), typeof(SessionStorage));
                    services.AddHostedService<Services.ModelObserver>();
                    services.AddHostedService<Services.SessionExpiry>();
                });
    }
}
