using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

namespace DuetWebServer
{
    /// <summary>
    /// Class used to start the ASP.NET Core endpoint
    /// </summary>
    public class Startup
    {
        /// <summary>
        /// Copy of the app configuration
        /// </summary>
        private readonly IConfiguration _configuration;

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
        /// <param name="services">Service collection</param>
        public static void ConfigureServices(IServiceCollection services)
        {
            services.AddCors(options => options.AddDefaultPolicy(Services.ModelObserver.CorsPolicy));
            services.AddControllers();
        }

        /// <summary>
        /// Configure the HTTP request pipeline
        /// </summary>
        /// <param name="app">Application builder</param>
        /// <param name="env">Hosting environment</param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
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
            app.UseRouting();

            // Enable CORS policy
            app.UseCors();

            // Define a keep-alive interval for operation as a reverse proxy
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(_configuration.GetValue("KeepAliveInterval", 30)),
            });

            // Define endpoints
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute("WebSocket", "{controller=WebSocket}");
                endpoints.MapControllerRoute("default", "{controller=Machine}");
            });

            // Use middleware for third-pary HTTP requests
            app.UseMiddleware(typeof(Middleware.CustomEndpointMiddleware));

            // Use fallback middlware
            app.UseMiddleware(typeof(Middleware.FallbackMiddleware));

            // Use static files from 0:/www if applicable
            if (_configuration.GetValue("UseStaticFiles", true))
            {
                app.UseStaticFiles();
                app.UseFileServer(new FileServerOptions
                {
                    FileProvider = new FileProviders.DuetFileProvider()
                });
            }
        }
    }
}
