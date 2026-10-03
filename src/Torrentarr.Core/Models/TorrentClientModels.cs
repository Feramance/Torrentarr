using Newtonsoft.Json;

namespace Torrentarr.Core.Models;

public sealed record TorrentClientIdentity(string InstanceId, string Type);

public sealed record TorrentClientCapabilities(
    bool Tags,
    bool Categories,
    bool TrackerMutation,
    bool FileEnumeration,
    bool Recheck,
    bool AutomaticManagement,
    bool PrivateState,
    bool PerTorrentLimits,
    bool SuperSeeding,
    bool ResumeDataExport,
    bool ContentDeletion)
{
    public static TorrentClientCapabilities None { get; } = new(
        false, false, false, false, false, false, false, false, false, false, false);

    public static TorrentClientCapabilities QBittorrent { get; } = new(
        Tags: true,
        Categories: true,
        TrackerMutation: true,
        FileEnumeration: true,
        Recheck: true,
        AutomaticManagement: true,
        PrivateState: true,
        PerTorrentLimits: true,
        SuperSeeding: true,
        ResumeDataExport: false,
        ContentDeletion: true);
}

public class TorrentClientCategory
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("savePath")]
    public string SavePath { get; set; } = "";
}

public class TorrentPropertiesRecord
{
    [JsonProperty("save_path")]
    public string SavePath { get; set; } = "";

    [JsonProperty("creation_date")]
    public long CreationDate { get; set; }

    [JsonProperty("piece_size")]
    public long PieceSize { get; set; }

    [JsonProperty("comment")]
    public string Comment { get; set; } = "";

    [JsonProperty("total_wasted")]
    public long TotalWasted { get; set; }

    [JsonProperty("total_uploaded")]
    public long TotalUploaded { get; set; }

    [JsonProperty("total_downloaded")]
    public long TotalDownloaded { get; set; }

    [JsonProperty("up_limit")]
    public long UpLimit { get; set; }

    [JsonProperty("dl_limit")]
    public long DlLimit { get; set; }

    [JsonProperty("time_elapsed")]
    public long TimeElapsed { get; set; }

    [JsonProperty("seeding_time")]
    public long SeedingTime { get; set; }

    [JsonProperty("nb_connections")]
    public int NbConnections { get; set; }

    [JsonProperty("share_ratio")]
    public double ShareRatio { get; set; }

    [JsonProperty("addition_date")]
    public long AdditionDate { get; set; }

    [JsonProperty("completion_date")]
    public long CompletionDate { get; set; }
}

public class TorrentFileRecord
{
    [JsonProperty("index")]
    public int Index { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("progress")]
    public double Progress { get; set; }

    [JsonProperty("priority")]
    public int Priority { get; set; }

    [JsonProperty("is_seed")]
    public bool IsSeed { get; set; }

    [JsonProperty("piece_range")]
    public List<int> PieceRange { get; set; } = new();

    [JsonProperty("availability")]
    public double Availability { get; set; }
}

public class TorrentTransferRecord
{
    [JsonProperty("dl_info_speed")]
    public long DownloadSpeed { get; set; }

    [JsonProperty("dl_info_data")]
    public long DownloadedData { get; set; }

    [JsonProperty("up_info_speed")]
    public long UploadSpeed { get; set; }

    [JsonProperty("up_info_data")]
    public long UploadedData { get; set; }

    [JsonProperty("dl_rate_limit")]
    public long DownloadRateLimit { get; set; }

    [JsonProperty("up_rate_limit")]
    public long UploadRateLimit { get; set; }

    [JsonProperty("dht_nodes")]
    public long DhtNodes { get; set; }

    [JsonProperty("connection_status")]
    public string ConnectionStatus { get; set; } = "";

    [JsonProperty("free_space_on_disk")]
    public long FreeSpaceOnDisk { get; set; }

    [JsonProperty("total_peer_connections")]
    public long TotalPeerConnections { get; set; }
}

public class TorrentClientSnapshot
{
    [JsonProperty("rid")]
    public long Revision { get; set; }

    [JsonProperty("full_update")]
    public bool? FullUpdate { get; set; }

    [JsonProperty("torrents")]
    public Dictionary<string, TorrentInfo>? Torrents { get; set; }

    [JsonProperty("torrents_removed")]
    public List<string>? TorrentsRemoved { get; set; }

    [JsonProperty("categories")]
    public Dictionary<string, TorrentClientCategory>? Categories { get; set; }

    [JsonProperty("categories_removed")]
    public List<string>? CategoriesRemoved { get; set; }

    [JsonProperty("tags")]
    public List<string>? Tags { get; set; }

    [JsonProperty("tags_removed")]
    public List<string>? TagsRemoved { get; set; }
}
