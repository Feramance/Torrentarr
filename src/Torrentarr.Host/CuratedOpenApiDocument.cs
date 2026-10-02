namespace Torrentarr.Host;

/// <summary>Serves the curated OpenAPI document shipped with the Host (tracked against qBitrr latest master).</summary>
public static class CuratedOpenApiDocument
{
    private static readonly Lazy<string> Json = new(Load);

    public static string GetJson() => Json.Value;

    private static string Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "openapi.json");
        if (File.Exists(path))
            return File.ReadAllText(path);

        using var resource = typeof(CuratedOpenApiDocument).Assembly
            .GetManifestResourceStream("openapi.json")
            ?? throw new FileNotFoundException("Curated OpenAPI spec is neither beside the Host binary nor embedded.", path);
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    public static IResult ServeJson(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Content(GetJson(), "application/json");
    }

    public static IResult RedirectToSwagger(string specPath)
    {
        var encoded = Uri.EscapeDataString(specPath);
        return Results.Redirect($"/swagger/index.html?url={encoded}");
    }
}
