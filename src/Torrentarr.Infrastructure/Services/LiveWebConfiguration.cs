using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Kestrel endpoint reload and request-time settings, in the existing process.</summary>
public sealed class LiveWebConfiguration
{
    private readonly IConfigurationRoot _endpoints;
    private WebUIConfig _applied;
    private WebUIConfig _bound;
    private long _listenerGeneration;
    private long _listenerRevision;
    private long _authRevision;
    private static string AuthenticationSettings(WebUIConfig settings) => Newtonsoft.Json.JsonConvert.SerializeObject(new
    { settings.AuthDisabled, settings.LocalAuthEnabled, settings.Token, settings.Username, settings.PasswordHash, settings.OIDCEnabled, settings.OIDC });
    public LiveWebConfiguration(IWebHostBuilder webHost, WebUIConfig initial)
    {
        _applied = initial;
        _bound = initial;
        _endpoints = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Endpoints:WebUI:Url"] = Url(initial) }).Build();
        webHost.ConfigureKestrel(options => options.Configure(_endpoints, reloadOnChange: true));
    }
    private static string Url(WebUIConfig config) => $"http://{(config.Host.Contains(':') ? "[" + config.Host.Trim('[', ']') + "]" : config.Host)}:{config.Port}";

    public void Attach(WebApplication app, TorrentarrConfig config)
    {
        var reloader = app.Services.GetRequiredService<ConfigReloader>();
        // TestServer does not own a listening socket.
        var listener = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .GetType().Name.Contains("Kestrel", StringComparison.OrdinalIgnoreCase);
        reloader.ValidateCandidate += candidate =>
        {
            if (!listener || candidate.WebUI.Port == _bound.Port) return null;
            try
            {
                var socket = new TcpListener(IPAddress.Parse(candidate.WebUI.Host), candidate.WebUI.Port);
                try { socket.Start(); } finally { socket.Stop(); }
                return null;
            }
            catch (SocketException) { return "The requested WebUI address is unavailable; the current listener remains active."; }
        };
        reloader.ConfigReloaded += (_, args) =>
        {
            if (!args.Success) return;
            var next = config.WebUI;
            if (next.Host != _applied.Host || next.Port != _applied.Port)
            {
                _endpoints["Endpoints:WebUI:Url"] = Url(next);
                _endpoints.Reload();
            }
            if (AuthenticationSettings(next) != AuthenticationSettings(_applied)) Interlocked.Exchange(ref _authRevision, args.Revision);
            _applied = next;
            app.Services.GetService<IOptionsMonitorCache<CookieAuthenticationOptions>>()?.Clear();
            var generation = Interlocked.Increment(ref _listenerGeneration);
            if (listener && !app.Urls.Contains(Url(next)))
            {
                reloader.Acknowledge("webui", _listenerRevision);
                _ = ObserveListenerAsync(app, reloader, next, args.Revision, generation);
            }
            else
            {
                _bound = next;
                Interlocked.Exchange(ref _listenerRevision, args.Revision);
                reloader.Acknowledge("webui", args.Revision);
            }
        };
        app.MapGet("/web/config/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var authentication = Interlocked.Read(ref _authRevision);
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var current = config.WebUI;
                    var payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        application = reloader.GetStatus(),
                        webui = new
                        {
                            authRevision = Interlocked.Read(ref _authRevision),
                            host = Volatile.Read(ref _bound).Host,
                            port = Volatile.Read(ref _bound).Port,
                            urlBase = UrlBaseHelper.NormalizeUrlBase(current.UrlBase),
                            behindHttpsProxy = current.BehindHttpsProxy,
                            theme = current.Theme,
                            viewDensity = current.ViewDensity,
                            liveArr = current.LiveArr
                        }
                    }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    await context.Response.WriteAsync("data: " + payload + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (authentication != Interlocked.Read(ref _authRevision)) return;
                    await reloader.WaitForChangeAsync(reloader.Revision, TimeSpan.FromSeconds(2), context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        app.Use(async (context, next) =>
        {
            var current = config.WebUI;
            if (current.BehindHttpsProxy)
            {
                var forwarded = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
                    app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                    Options.Create(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto }));
                await forwarded.Invoke(context);
            }
            var pathBase = new PathString(UrlBaseHelper.NormalizeUrlBase(current.UrlBase));
            var originalPath = context.Request.Path;
            var originalBase = context.Request.PathBase;
            if (pathBase.HasValue && context.Request.Path.StartsWithSegments(pathBase, out var remaining))
            {
                context.Request.PathBase = originalBase.Add(pathBase);
                context.Request.Path = remaining;
            }
            try { await next(context); }
            finally { context.Request.Path = originalPath; context.Request.PathBase = originalBase; }
        });
        app.UseRouting();
    }
    private async Task ObserveListenerAsync(WebApplication app, IConfigReloader reloader, WebUIConfig desired, long revision, long generation)
    {
        var ct = app.Lifetime.ApplicationStopping;
        try
        {
            while (!ct.IsCancellationRequested && Interlocked.Read(ref _listenerGeneration) == generation)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && Interlocked.Read(ref _listenerGeneration) == generation)
                {
                    if (app.Urls.Contains(Url(desired)))
                    {
                        Volatile.Write(ref _bound, desired);
                        Interlocked.Exchange(ref _listenerRevision, revision);
                        reloader.Acknowledge("webui", revision);
                        return;
                    }
                    await Task.Delay(100, ct);
                }
                if (Interlocked.Read(ref _listenerGeneration) != generation) return;
                _endpoints["Endpoints:WebUI:Url"] = Url(_bound);
                _endpoints.Reload();
                reloader.Acknowledge("webui", _listenerRevision, "Listener change failed; restoring the previous address and retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (Interlocked.Read(ref _listenerGeneration) != generation) return;
                _endpoints["Endpoints:WebUI:Url"] = Url(desired);
                _endpoints.Reload();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            reloader.Acknowledge("webui", _listenerRevision, "Listener configuration could not be applied.");
        }
    }

}
