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
            object source;
            if (config.QBitInstances.TryGetValue(rename.Name, out var client)
                && (destination.Equals("qBit", StringComparison.OrdinalIgnoreCase) || destination.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase))
                && !config.QBitInstances.ContainsKey(destination)) source = client;
            else if (config.ArrInstances.TryGetValue(rename.Name, out var arr)
                && ArrSectionHelper.ArrTypeFromSectionName(destination) == ArrSectionHelper.ArrTypeFromSectionName(rename.Name)
                && !config.ArrInstances.ContainsKey(destination)) source = arr;
            else throw new ArgumentException("Invalid configuration rename.");
            if (destination.Contains('.')) throw new ArgumentException("Invalid configuration rename.");

            var sourceObject = JObject.FromObject(source);
            Preserve(sourceObject, destination);
        }

        void Preserve(JObject source, string prefix)
        {
            foreach (var property in source.Properties())
            {
                var path = prefix + "." + property.Name;
                var supplied = changes[path] ?? changes.SelectToken(path);
                if (property.Name is "Password" or "APIKey" or "Token")
                {
                    if (supplied == null || supplied.Type == JTokenType.String && supplied.Value<string>() == "[redacted]")
                        changes[path] = property.Value.DeepClone();
                }
                else if (property.Value is JObject nested) Preserve(nested, path);
                else if (property.Name == "Notifications" && property.Value is JArray original && supplied is JArray notifications)
                    foreach (var notification in notifications.OfType<JObject>())
                        if (notification.GetValue("Token", StringComparison.OrdinalIgnoreCase)?.Value<string>() == "[redacted]")
                        {
                            var existing = original.OfType<JObject>().FirstOrDefault(n =>
                                string.Equals(n["Url"]?.Value<string>(), notification.GetValue("Url", StringComparison.OrdinalIgnoreCase)?.Value<string>(), StringComparison.OrdinalIgnoreCase));
                            if (existing == null) throw new ArgumentException("Cannot restore a notification token for the renamed client.");
                            notification.Properties().First(p => p.Name.Equals("Token", StringComparison.OrdinalIgnoreCase)).Value = existing["Token"]?.DeepClone() ?? JValue.CreateNull();
                        }
            }
        }
    }
}
