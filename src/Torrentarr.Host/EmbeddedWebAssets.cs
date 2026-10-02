using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace Torrentarr.Host;

/// <summary>Combines development files with the embedded SPA used by standalone releases.</summary>
internal static class EmbeddedWebAssets
{
    public static IFileProvider Create(IWebHostEnvironment environment, Assembly assembly)
    {
        var providers = new List<IFileProvider>();

        if (!string.IsNullOrWhiteSpace(environment.WebRootPath) && Directory.Exists(environment.WebRootPath))
            providers.Add(new PhysicalFileProvider(environment.WebRootPath));

        try
        {
            providers.Add(new ManifestEmbeddedFileProvider(assembly, "wwwroot"));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            // Source/API-only builds may not contain a frontend bundle or manifest.
        }

        return providers.Count switch
        {
            0 => new NullFileProvider(),
            1 => providers[0],
            _ => new CompositeFileProvider(providers)
        };
    }
}
