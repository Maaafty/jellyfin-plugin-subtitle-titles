using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal static partial class EpisodeNumber
{
    internal static int? Resolve(Episode episode, string? path)
        => episode.IndexNumber ?? FromFilename(episode.ParentIndexNumber, episode.IndexNumberEnd, path);

    internal static int? FromFilename(int? season, int? end, string? path)
    {
        if (season is not > 0 || end is not null || string.IsNullOrEmpty(path)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var matches = Number().Matches(stem);
        if (matches.Count != 1) return null;
        var match = matches[0];
        if (Parse(match.Groups[1]) != season || RangeSuffix().IsMatch(stem.AsSpan(match.Index + match.Length))) return null;
        var number = Parse(match.Groups[2]);
        return number > 0 ? number : null;
    }

    private static int Parse(Group value) => int.Parse(value.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![a-zA-Z0-9])[Ss]([0-9]{1,3})[Ee]([0-9]{1,4})(?![a-zA-Z0-9])")]
    private static partial Regex Number();

    // Jellyfin also accepts ranges such as S01E01-x02 and S01E01-xe02.
    [GeneratedRegex(@"^\s*(?:(?:[-+~&–—_,.]\s*)?(?:[Ee]\s*[0-9]|[0-9]{1,4}(?:\b|$)|[0-9]{1,3}[xX][0-9])|-\s*[xX][Ee]?[0-9])")]
    private static partial Regex RangeSuffix();
}
