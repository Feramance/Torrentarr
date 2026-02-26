using Newtonsoft.Json;

namespace Torrentarr.Core.Models;

/// <summary>
/// Domain model for torrent information
/// </summary>
public class TorrentInfo
{
    [JsonProperty("hash")]
    public string Hash { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("progress")]
    public double Progress { get; set; }

    [JsonProperty("state")]
    public string State { get; set; } = "";

    [JsonProperty("category")]
    public string Category { get; set; } = "";

    [JsonProperty("ratio")]
    public double Ratio { get; set; }

    [JsonProperty("seeding_time")]
    public long SeedingTime { get; set; }

    [JsonProperty("added_on")]
    public long AddedOn { get; set; }

    [JsonProperty("completion_on")]
    public long CompletionOn { get; set; }

    [JsonProperty("save_path")]
    public string SavePath { get; set; } = "";

    [JsonProperty("tracker")]
    public string Tracker { get; set; } = "";

    [JsonProperty("uploaded")]
    public long Uploaded { get; set; }

    [JsonProperty("downloaded")]
    public long Downloaded { get; set; }

    [JsonProperty("tags")]
    public string Tags { get; set; } = "";

    /// <summary>
    /// The name of the QBitInstances key this torrent was fetched from (e.g. "qBit", "qBit-seedbox").
    /// Not from JSON — set by the code that fetches the torrent from a specific qBit client.
    /// </summary>
    [JsonIgnore]
    public string QBitInstanceName { get; set; } = "qBit";

    [JsonProperty("content_path")]
    public string ContentPath { get; set; } = "";

    [JsonProperty("amount_left")]
    public long AmountLeft { get; set; }

    [JsonProperty("availability")]
    public double Availability { get; set; }

    [JsonProperty("eta")]
    public long Eta { get; set; }

    [JsonProperty("last_activity")]
    public long LastActivity { get; set; }

    /// <summary>
    /// Check if torrent state indicates uploading/seeding.
    /// Matches qBitrr's is_uploading check.
    /// </summary>
    public bool IsUploading => !string.IsNullOrEmpty(State) && (
        State.Contains("uploading", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("stalledupload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("queuedupload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("pausedupload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("forcedupload", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if torrent state indicates downloading.
    /// Matches qBitrr's is_downloading check.
    /// </summary>
    public bool IsDownloading => !string.IsNullOrEmpty(State) && (
        State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("stalleddownload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("queueddownload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("pauseddownload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("forceddownload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("metadata", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if torrent is stopped (not just paused).
    /// </summary>
    public bool IsStopped => !string.IsNullOrEmpty(State) && (
        State.Equals("stoppeddownload", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("stoppedupload", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("stopped", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Domain model for torrent tracker information
/// </summary>
public class TorrentTracker
{
    [JsonProperty("url")]
    public string Url { get; set; } = "";

    [JsonProperty("status")]
    public int Status { get; set; }

    [JsonProperty("tier")]
    public int Tier { get; set; }

    [JsonProperty("num_peers")]
    public int NumPeers { get; set; }

    [JsonProperty("num_seeds")]
    public int NumSeeds { get; set; }

    [JsonProperty("num_leeches")]
    public int NumLeeches { get; set; }

    [JsonProperty("num_downloaded")]
    public int NumDownloaded { get; set; }

    [JsonProperty("msg")]
    public string Msg { get; set; } = "";
}
