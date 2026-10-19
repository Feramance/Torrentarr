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
            if (args.Success) UpdateListener(config.WebUI);
        };
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
