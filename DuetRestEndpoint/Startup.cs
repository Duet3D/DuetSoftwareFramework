using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DuetRestEndpoint
{
    /// <summary>
    /// Class used to start the ASP.NET Core endpoint
    /// </summary>
    public class Startup
    {
        private IConfiguration _configuration;

        /// <summary>
        /// Create a new Startup instance
        /// </summary>
        /// <param name="configuration">Launch configuration (see appsettings.json)</param>
        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Configure web services and add service to the container
        /// </summary>
        /// <param name="services"></param>
        public void ConfigureServices(IServiceCollection services)
        {
            // Register CORS policy (may or may not be used)
            services.AddCors(options =>
            {
                options.AddPolicy("cors-localhost",
                builder =>
                {
                    // Allow very unrestrictive CORS requests (for now)
                    builder
                        .AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

            // Register service to keep the machine model up-to-date
            Services.ModelUpdateService.SocketPath = _configuration.GetValue("SocketPath", "/tmp/duet.sock");
            services.AddHostedService<Services.ModelUpdateService>();

            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);
        }

        /// <summary>
        /// Configure the HTTP request pipeline
        /// </summary>
        /// <param name="app">Application builder</param>
        /// <param name="env">Hosting environment</param>
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // Set flags to act as a reverse proxy for Apache or nginx
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            // Set CORS flags if applicable
            if (_configuration.GetValue("UseCors", true))
            {
                app.UseCors("cors-localhost");
            }

            // Use WebSockets and MVC architecture
            app.UseWebSockets();
            app.UseMvc();
        }
    }
}
