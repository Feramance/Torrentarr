using Torrentarr.Core.Configuration;

namespace Torrentarr.Core.Services;

public sealed record ConfigurationRename(long Revision, Dictionary<string, string> Names);
public sealed record ConfigurationSnapshot(long Revision, TorrentarrConfig Config, Dictionary<string, string>? Renames = null,
    IReadOnlyList<ConfigurationRename>? RenameHistory = null);
public sealed record ConfigurationComponentStatus(long AppliedRevision, string? Error = null);
public sealed record ConfigurationApplicationStatus(long AcceptedRevision, string State,
    IReadOnlyDictionary<string, ConfigurationComponentStatus> Components, string? Error);
