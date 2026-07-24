using DuetWebServer.Middleware;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System;

namespace DuetWebServer;

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
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

        // Load the DSF configuration file (overridable via --config) plus command-line overrides last
        string configFile = DefaultConfigFile;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config")
            {
                configFile = args[i + 1];
                break;
            }
        }
        builder.Configuration.AddJsonFile(configFile, false, true);
        builder.Configuration.AddCommandLine(args);

        // The slim builder starts from Kestrel core, which cannot read the certificate of an HTTPS
        // endpoint declared in the Kestrel configuration section until this is registered
        builder.WebHost.UseKestrelHttpsConfiguration();

        builder.Host.UseSystemd();

        // Application settings are bound at the configuration root
        builder.Services.AddOptions<Settings>().Bind(builder.Configuration);

        // Singletons and hosted services
        builder.Services.AddSingleton<IModelProvider, ModelProvider>();
        builder.Services.AddSingleton<ISessionStorage, SessionStorage>();
        builder.Services.AddHostedService<Services.ModelObserver>();
        builder.Services.AddHostedService<Services.SessionExpiry>();

        // Custom middlewares resolved from DI as singletons via IMiddleware
        builder.Services.AddSingleton<CustomEndpointMiddleware>();
        builder.Services.AddSingleton<FallbackMiddleware>();

        // Session-key authentication and access policies
        builder.Services
            .AddAuthentication(Authorization.SessionKeyAuthenticationHandler.SchemeName)
            .AddScheme<Authorization.SessionKeyAuthenticationSchemeOptions, Authorization.SessionKeyAuthenticationHandler>(Authorization.SessionKeyAuthenticationHandler.SchemeName, options => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Authorization.Policies.ReadOnly, policy => policy.RequireClaim("access", "readOnly", "readWrite"))
            .AddPolicy(Authorization.Policies.ReadWrite, policy => policy.RequireClaim("access", "readWrite"));
        builder.Services.AddCors(options => options.AddDefaultPolicy(Services.ModelObserver.CorsPolicy));

        WebApplication app = builder.Build();
        Settings settings = app.Services.GetRequiredService<IOptions<Settings>>().Value;

        // Act as a reverse proxy for Apache or nginx
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
            KeepAliveInterval = TimeSpan.FromSeconds(settings.KeepAliveInterval)
        });

        // Third-party HTTP requests and the SPA fallback only see requests that no mapped endpoint matched
        app.UseMiddleware<CustomEndpointMiddleware>();
        app.UseMiddleware<FallbackMiddleware>();

        // Serve static files if enabled
        if (settings.UseStaticFiles)
        {
            // Provide files either using the directory provided by directories.web or from the override directory
            IFileProvider fileProvider = (settings.OverrideWebDirectory is not null)
                ? new PhysicalFileProvider(settings.OverrideWebDirectory)
                : new FileProviders.DuetFileProvider(app.Services.GetRequiredService<IModelProvider>());

            // A matched endpoint terminates before the static-file middleware runs
            app.UseWhen(context => context.GetEndpoint() is null, appBuilder =>
            {
                // Configure file provider; don't cache the index page but cache all other assets
                appBuilder.UseFileServer(new FileServerOptions
                {
                    FileProvider = fileProvider,
                    StaticFileOptions =
                    {
                        OnPrepareResponse = ctx =>
                        {
                            if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                            {
                                ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-store,no-cache,must-revalidate";
                                ctx.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
                            }
                            else
                            {
                                ctx.Context.Response.Headers[HeaderNames.CacheControl] = $"public,max-age={settings.MaxAge},must-revalidate";
                            }
                        }
                    }
                });
            });
        }

        // Map the HTTP and WebSocket endpoints
        Endpoints.MachineEndpoints.Map(app);
        Endpoints.RepRapFirmwareEndpoints.Map(app);
        Endpoints.WebSocketEndpoint.Map(app);

        app.Run();
    }
}
