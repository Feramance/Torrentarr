namespace Torrentarr.Core.Configuration;

public sealed record ConfigurationSnapshot(long Revision, TorrentarrConfig Config,
    Dictionary<string, string>? Renames = null, List<ConfigurationRename>? RenameHistory = null);

public sealed record ConfigurationRename(long Revision, string From, string To);
