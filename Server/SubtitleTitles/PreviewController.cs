using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MaftySubtitleTitles/Results")]
public sealed class PreviewController(IApplicationPaths paths, ILibraryManager library, IMediaStreamRepository repository) : ControllerBase
{
    public sealed record Source(Guid Id, string Name, string? File, int? Season, int? Episode,
        bool HasSubtitleResult, Guid? DiscItemId = null);
    public sealed record Poster(Guid Id, string Name, int? Year, Guid? ImageId, bool IsSeries, Source[] Sources);
    public sealed record Catalog(bool TextAvailable, bool BitmapAvailable, Poster[] Posters);
    public sealed record Subtitle(int Index, string Codec, string? Original, string? Suggested, string? Previous,
        string Status, bool Reused, string? Effects, string? DeclaredLanguage, string? DetectedLanguage,
        string? Current, string? Reason);

    [HttpGet]
    public Catalog Get(CancellationToken ct)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var selected = plugin.Configuration.LibraryIds.ToHashSet();
        Dictionary<Guid, (BaseItem Item, bool IsSeries, Dictionary<Guid, Source> Sources)> groups = [];
        if (selected.Count != 0 && Directory.Exists(StateFolder))
        {
            foreach (var file in new DirectoryInfo(StateFolder).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id)
                    || library.GetItemById(id) is not { } item || !item.GetAncestorIds().Any(selected.Contains)) continue;
                AddSource(item, id, item.Path, hasSubtitleResult: true);
            }
        }
        if (selected.Count != 0)
        {
            var query = new InternalItemsQuery { AncestorIds = selected.ToArray(), Recursive = true, IsVirtualItem = false,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], Limit = 200 };
            var seen = new HashSet<Guid>();
            int start = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                query.StartIndex = start;
                var items = library.GetItemList(query);
                if (items.Count == 0) break;
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var source in item.GetMediaSources(false))
                    {
                        if (!DiscVolume.IsDisc(source.Path) || !Guid.TryParse(source.Id, out var id) || !seen.Add(id)) continue;
                        var version = id != item.Id && library.GetItemById(id) is Video video
                            && video.GetAncestorIds().Any(selected.Contains) ? video : item;
                        AddSource(version, id, source.Path, hasSubtitleResult: false, discItemId: item.Id);
                    }
                }
                start += items.Count;
            }
        }
        var posters = groups.Values.Select(group => new Poster(group.Item.Id, group.Item.Name, group.Item.ProductionYear,
            group.Item.HasImage(ImageType.Primary) ? group.Item.Id : null, group.IsSeries,
            group.Sources.Values.OrderBy(source => source.Season ?? int.MaxValue)
                .ThenBy(source => source.Episode ?? int.MaxValue).ThenBy(source => source.File, StringComparer.Ordinal).ToArray())).ToArray();
        return new(plugin.Tools.Available, plugin.Tools.BitmapAvailable, posters);

        void AddSource(BaseItem item, Guid id, string? path, bool hasSubtitleResult, Guid? discItemId = null)
        {
            var episode = item as Episode;
            var poster = episode?.Series ?? (item is Video { PrimaryVersionId: { } primaryId }
                ? library.GetItemById(primaryId) ?? item : item);
            if (!groups.TryGetValue(poster.Id, out var group))
                groups.Add(poster.Id, group = (poster, episode is not null, []));
            group.Sources[id] = group.Sources.TryGetValue(id, out var existing)
                ? existing with { DiscItemId = discItemId }
                : new(id, item.Name, Path.GetFileName(path), episode?.ParentIndexNumber,
                    episode is null ? null : EpisodeNumber.Resolve(episode, path), hasSubtitleResult, discItemId);
        }
    }

    [HttpGet("{sourceId:guid}")]
    public async Task<ActionResult<Subtitle[]>> GetTracks(Guid sourceId, CancellationToken ct)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var selected = plugin.Configuration.LibraryIds.ToHashSet();
        if (selected.Count == 0 || library.GetItemById(sourceId) is not { } item
            || !item.GetAncestorIds().Any(selected.Contains)) return NotFound();
        var path = Path.Combine(StateFolder, sourceId.ToString("N") + ".json");
        FileStream file;
        try { file = System.IO.File.OpenRead(path); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        await using var opened = file;
        var record = await JsonSerializer.DeserializeAsync<ScanTask.ScanRecord>(opened, cancellationToken: ct).ConfigureAwait(false);
        if (record is null) return NotFound();
        var streams = repository.GetMediaStreams(new MediaStreamQuery { ItemId = sourceId });
        var identities = record.TitleHistory is { Length: > 0 } && item.Path is { } sourcePath
            ? ScanTask.UniqueIdentities(sourcePath, streams) : null;
        return streams.Where(stream => stream.Type == MediaStreamType.Subtitle)
            .OrderBy(stream => stream.Index)
            .Select(stream => DescribeTrack(record, identities?.GetValueOrDefault(stream.Index), stream)).ToArray();
    }

    [HttpGet("{itemId:guid}/{sourceId:guid}/Disc")]
    public async Task<ActionResult<DiscEditionCatalog>> GetDisc(Guid itemId, Guid sourceId, CancellationToken ct)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var selected = plugin.Configuration.LibraryIds.ToHashSet();
        if (selected.Count == 0 || library.GetItemById(itemId) is not { } item
            || !item.GetAncestorIds().Any(selected.Contains)) return NotFound();
        var source = item.GetMediaSources(false).FirstOrDefault(source => Guid.TryParse(source.Id, out var id) && id == sourceId);
        if (source?.Path is not { } path || !DiscVolume.IsDisc(path)) return NotFound();
        return await new DiscEditionStore(paths.DataPath).Describe(path, sourceId, ct).ConfigureAwait(false);
    }

    private static Subtitle DescribeTrack(ScanTask.ScanRecord record, ScanTask.TrackIdentity? identity, MediaStream stream)
    {
        var codec = stream.Codec ?? "";
        var current = stream.Title;
        var format = ScanTask.SubtitleFormat(stream.Codec);
        var reason = (format is null ? null : record.SourceIssue)
            ?? record.Issues?.FirstOrDefault(issue => issue.Index == stream.Index && issue.Codec == stream.Codec)?.Reason
            ?? (format is null ? "unsupported" : null);
        if (reason is not null)
        {
            var issueStatus = reason is "bitmapDisabled" or "missingDuration" or "unsupported" ? "skipped" : "failed";
            return NoEvidence(issueStatus, reason);
        }

        var history = identity is not null && record.TitleHistory is { } changes
            ? Array.Find(changes, change => change.Track == identity) : null;
        bool owned = history is not null && ScanTask.OwnsTitle(history, current);
        var track = Array.Find(record.Tracks, edit => edit.Index == stream.Index && edit.Codec == stream.Codec);
        if (track is null) return NoEvidence("notScanned", null, owned ? history!.Original : null);

        var original = history is null ? track.Original : history.Original;
        // Modern records never infer ownership from the evidence's proposed or previous title.
        bool legacyMatch = record.TitleHistory is null
            && (current == track.Proposed || (track.Previous is not null && current == track.Previous));
        bool manual = current != original && !owned && !legacyMatch;
        bool insufficient = track.Effect is null || track.BitmapInconclusive;
        var status = manual ? "manual"
            : insufficient ? "insufficient"
            : track.Conflict ? "conflict"
            : current != track.Proposed ? "changed"
            : current != original ? "applied"
            : "unchanged";
        var (declared, detected) = TitleRules.DescribeEvidence(original, track.Language, track.Languages ?? "");
        return new Subtitle(stream.Index, codec, original, manual || insufficient ? null : track.Proposed, track.Previous,
            status, track.Reused, DescribeEffects(codec, track.Effect), declared, detected, current, null);

        Subtitle NoEvidence(string rowStatus, string? rowReason, string? rowOriginal = null) => new(stream.Index, codec, rowOriginal, null, null, rowStatus,
            false, DescribeEffects(codec, null), null, null, current, rowReason);
    }

    internal static string? DescribeEffects(string? codec, AssEffects? effect) => ScanTask.SubtitleFormat(codec) switch
    {
        "ass" => effect?.ToString() ?? "NotChecked",
        "sup" or "dvd" or "dvb" or "xsub" => "NotChecked",
        _ => null,
    };

    private string StateFolder => Path.Combine(paths.DataPath, ScanTask.StateDirectory);
}
