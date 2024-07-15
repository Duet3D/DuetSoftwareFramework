using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using System;

namespace DuetWebServer
{
    /// <summary>
    /// Class used to start the ASP.NET Core endpoint
    /// </summary>
    /// <remarks>
    /// Create a new Startup instance
    /// </remarks>
    /// <param name="configuration">Launch configuration (see appsettings.json)</param>
    public class Startup(IConfiguration configuration)
    {
        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings = configuration.Get<Settings>() ?? new();

        /// <summary>
        /// Configure web services and add service to the container
        /// </summary>
        /// <param name="services">Service collection</param>
        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddAuthentication(Authorization.SessionKeyAuthenticationHandler.SchemeName)
                .AddScheme<Authorization.SessionKeyAuthenticationSchemeOptions, Authorization.SessionKeyAuthenticationHandler>(Authorization.SessionKeyAuthenticationHandler.SchemeName, options => {});
            services.AddAuthorization(options =>
            {
                options.AddPolicy(Authorization.Policies.ReadOnly, policy => policy.RequireClaim("access", "readOnly", "readWrite"));
                options.AddPolicy(Authorization.Policies.ReadWrite, policy => policy.RequireClaim("access", "readWrite"));
            });
            services.AddCors(options => options.AddDefaultPolicy(Services.ModelObserver.CorsPolicy));
            services.AddControllers();
        }

        /// <summary>
        /// Configure the HTTP request pipeline
        /// </summary>
        /// <param name="app">Application builder</param>
        /// <param name="env">Hosting environment</param>
        /// <param name="serviceProvider">Service provider</param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider serviceProvider)
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

            // Enable support for authentication and authorization
            app.UseAuthentication();
            app.UseAuthorization();

            // Define a keep-alive interval for operation as a reverse proxy
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(_settings.KeepAliveInterval),
            });

            // Use middleware to fix content types
            app.UseMiddleware(typeof(Middleware.FixContentTypeMiddleware));

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

            // Use static files if enabled
            if (_settings.UseStaticFiles)
            {
                // Don't cache the index page but cache all other assets
                app.UseStaticFiles(new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        if (ctx.Context.Request.Path.Equals("/"))
                        {
                            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-store,no-cache,must-revalidate";
                            ctx.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
                        }
                        else
                        {
                            ctx.Context.Response.Headers[HeaderNames.CacheControl] = $"public,max-age={_settings.MaxAge},must-revalidate";
                            ctx.Context.Response.Headers[HeaderNames.Expires] = "0";
                        }
                    }
                });

                // Provide files either using the directory provided by directories.web or from the override directory
                IFileProvider fileProvider;
                if (_settings.OverrideWebDirectory != null)
                {
                    fileProvider = new PhysicalFileProvider(_settings.OverrideWebDirectory);
                }
                else
                {
                    fileProvider = ActivatorUtilities.CreateInstance<FileProviders.DuetFileProvider>(serviceProvider);
                }

                // Configure file provider
                app.UseFileServer(new FileServerOptions
                {
                    FileProvider = fileProvider
                });
            }
        }
    }
}
