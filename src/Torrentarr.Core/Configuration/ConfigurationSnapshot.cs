namespace Torrentarr.Core.Configuration;

public sealed record ConfigurationSnapshot(long Revision, TorrentarrConfig Config,
    Dictionary<string, string>? Renames = null);
