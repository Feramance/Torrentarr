using System.Text.RegularExpressions;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

internal static class FileFilterMatcher
{
    internal static string? LocalReason(string filePath, TorrentConfig cfg, RegexOptions regexOptions)
    {
        // Normalize to forward slashes
        filePath = filePath.Replace('\\', '/');

        var parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Path has no filename.";
        var fileName = parts.Last();
        // The first component is the torrent's title, not an internal exclusion folder.
        var folderParts = parts.Skip(1).Take(Math.Max(0, parts.Length - 2));

        // Check folder exclusion regex against each path component
        foreach (var folder in folderParts)
        {
            foreach (var pattern in cfg.FolderExclusionRegex)
            {
                try
                {
                    if (Regex.IsMatch(folder, pattern, regexOptions, TimeSpan.FromMilliseconds(25)))
                        return $"Folder '{folder}' matched exclusion regex '{pattern}'.";
                }
                catch (ArgumentException) { /* skip bad regex patterns */ }
            }
        }

        // Check file name exclusion regex
        foreach (var pattern in cfg.FileNameExclusionRegex)
        {
            try
            {
                if (Regex.IsMatch(fileName, pattern, regexOptions, TimeSpan.FromMilliseconds(25)))
                    return $"Filename matched exclusion regex '{pattern}'.";
            }
            catch (ArgumentException) { /* skip bad regex patterns */ }
        }

        // Check file extension allowlist — if allowlist is non-empty, extension must be in it
        if (cfg.FileExtensionAllowlist.Count > 0)
        {
            if (!MediaValidationService.MatchesExtensionAllowlist(fileName, cfg.FileExtensionAllowlist))
                return $"Extension '{Path.GetExtension(fileName)}' is not allowed.";
        }

        return null;
    }

}
