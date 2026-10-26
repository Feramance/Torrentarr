using Newtonsoft.Json.Linq;

namespace Torrentarr.Core.Configuration;

public static class ConfigRenameHelper
{
    public static void PreserveSecrets(TorrentarrConfig config, JObject changes, JObject? renames)
    {
        if (renames == null) return;
        foreach (var rename in renames.Properties())
        {
            var destination = rename.Value.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(destination) || destination.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Invalid configuration rename.");
            object source;
            var prefix = destination;
            if (config.TorrentClients.TryGetValue(rename.Name, out var canonical)
                && !config.GetAllTorrentClients().ContainsKey(destination))
            {
                source = canonical;
                prefix = "TorrentClient." + destination;
            }
            else if (config.QBitInstances.TryGetValue(rename.Name, out var client)
                && (destination.Equals("qBit", StringComparison.OrdinalIgnoreCase) || destination.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase))
                && !config.QBitInstances.ContainsKey(destination)) source = client;
            else if (config.ArrInstances.TryGetValue(rename.Name, out var arr)
                && ArrSectionHelper.ArrTypeFromSectionName(destination) == ArrSectionHelper.ArrTypeFromSectionName(rename.Name)
                && !config.ArrInstances.ContainsKey(destination)) source = arr;
            else throw new ArgumentException("Invalid configuration rename.");

            var sourceObject = JObject.FromObject(source);
            Preserve(sourceObject, prefix);
        }

        void Preserve(JObject source, string prefix)
        {
            foreach (var property in source.Properties())
            {
                var path = prefix + "." + property.Name;
                var supplied = changes.GetValue(path, StringComparison.OrdinalIgnoreCase);
                for (var split = path.LastIndexOf('.'); supplied == null && split > 0; split = path.LastIndexOf('.', split - 1))
                {
                    var nestedValue = changes.GetValue(path[..split], StringComparison.OrdinalIgnoreCase);
                    foreach (var part in path[(split + 1)..].Split('.'))
                        nestedValue = (nestedValue as JObject)?.GetValue(part, StringComparison.OrdinalIgnoreCase);
                    supplied = nestedValue;
                }
                if (property.Name is "Password" or "APIKey" or "Token")
                {
                    if (supplied == null || supplied.Type == JTokenType.String && supplied.Value<string>() == "[redacted]")
                        changes[path] = property.Value.DeepClone();
                }
                else if (property.Value is JObject nested) Preserve(nested, path);
            }
        }
    }
}
