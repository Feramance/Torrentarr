using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public sealed class LiveWebConfiguration
{
    private readonly ConcurrentDictionary<Guid, Func<Task>> _streams = new();
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly IConfigurationRoot _listener = new ConfigurationBuilder().AddInMemoryCollection().Build();

    public LiveWebConfiguration(IWebHostBuilder builder, WebUIConfig config)
    {
        UpdateListener(config);
        builder.ConfigureKestrel(options => options.Configure(_listener, reloadOnChange: true));
    }

    private void UpdateListener(WebUIConfig config)
    {
        var host = System.Net.IPAddress.Parse(config.Host);
        _listener["Endpoints:Http:Url"] = $"http://{(host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host.ToString())}:{config.Port}";
        _listener.Reload();
    }

    public void Attach(WebApplication app, TorrentarrConfig config)
    {
        var reloader = app.Services.GetRequiredService<IConfigReloader>();
        reloader.ConfigReloaded += (_, args) =>
        {
            if (args.Success) _ = ApplyListenerAsync();
        };
        async Task ApplyListenerAsync()
        {
            try
            {
                await _updates.WaitAsync(app.Lifetime.ApplicationStopping);
                try
                {
                    // Flush the destination to connected browsers before replacing the listener.
                    await Task.WhenAll(_streams.Values.Select(send => send()));
                    UpdateListener(config.WebUI);
                }
                finally { _updates.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Live WebUI listener update failed"); }
        }
        app.MapGet("/web/config/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var id = Guid.NewGuid();
            using var writing = new SemaphoreSlim(1, 1);
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            string? last = null;
            async Task SendAsync()
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await writing.WaitAsync(timeout.Token);
                    try
                    {
                        var web = config.WebUI;
                        var json = JsonSerializer.Serialize(new
                        {
                            application = reloader.GetStatus(),
                            webui = new
                            {
                                host = web.Host,
                                port = web.Port,
                                urlBase = UrlBaseHelper.NormalizeUrlBase(web.UrlBase),
                                behindHttpsProxy = web.BehindHttpsProxy,
                                theme = web.Theme,
                                viewDensity = web.ViewDensity,
                                liveArr = web.LiveArr
                            }
                        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (json == last) return;
                        await context.Response.WriteAsync($"data: {json}\n\n", timeout.Token);
                        await context.Response.Body.FlushAsync(timeout.Token);
                        last = json;
                    }
                    finally { writing.Release(); }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    _streams.TryRemove(id, out _);
                    stopped.Cancel();
                }
            }
            _streams[id] = SendAsync;
            try
            {
                await SendAsync();
                while (!stopped.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stopped.Token);
                    await SendAsync();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _streams.TryRemove(id, out _);
                // A broadcast may already own this callback; finish it before disposing its resources.
                await _updates.WaitAsync();
                _updates.Release();
            }
        });
        app.UseWhen(_ => config.WebUI.BehindHttpsProxy, branch => branch.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        }));
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var pathBase = context.Request.PathBase;
            var prefix = UrlBaseHelper.NormalizeUrlBase(config.WebUI.UrlBase);
            if (prefix.Length > 0 && path.StartsWithSegments(new PathString(prefix), out var matched, out var remaining))
            {
                context.Request.PathBase = pathBase.Add(matched);
                context.Request.Path = remaining;
            }
            try { await next(context); }
            finally { context.Request.Path = path; context.Request.PathBase = pathBase; }
        });
    }
}
