using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

public sealed class DiscScanTask(ILibraryManager library, IMediaEncoder encoder, IApplicationPaths paths, ILogger<DiscScanTask> log) : IScheduledTask
{
    public string Name => "Identify Dolby Vision on Blu-ray ISO";
    public string Key => "MaftyDiscVision";
    public string Description => "Inspect local Blu-ray ISO files in the plugin's selected libraries without decoding video.";
    public string Category => "Library";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var config = plugin.Configuration;
        var folders = library.GetVirtualFolders().Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty).ToHashSet();
        var selected = config.LibraryIds.Distinct().ToArray();
        if (selected.Length == 0 || selected.Any(id => id == Guid.Empty || !folders.Contains(id)))
        {
            log.LogWarning("Select media libraries in {PluginName} before running the Blu-ray ISO task", Plugin.DisplayName);
            return;
        }
        var cache = new DiscCache(paths.DataPath);
        var tools = plugin.Tools;
        var query = new InternalItemsQuery { AncestorIds = selected, Recursive = true, IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], Limit = 200 };
        int total = library.GetCount(query), done = 0;
        var seen = new HashSet<Guid>();
        while (true)
        {
            var items = library.GetItemList(query);
            if (items.Count == 0) break;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var source in item.GetMediaSources(false))
                {
                    if (!DiscCache.IsIso(source.Path) || !Guid.TryParse(source.Id, out var id) || !seen.Add(id)) continue;
                    var path = source.Path!;
                    DiscCache.Stamp? stamp = null;
                    try
                    {
                        stamp = DiscCache.Stamp.ForProbe(path);
                        if (!stamp.IsAvailable)
                        {
                            await cache.Write(id, stamp, new("file-unavailable"), cancellationToken).ConfigureAwait(false);
                            log.LogWarning("Blu-ray ISO skipped: {Title} [{File}, source {SourceId}] – file unavailable", item.Name, Path.GetFileName(path), id);
                            continue;
                        }
                        var cached = await cache.Read(id, stamp, cancellationToken).ConfigureAwait(false);
                        if (cached is not null && cached.Status is not ("failed" or "file-unavailable"))
                        {
                            log.LogInformation("Blu-ray ISO cached: {Title} [{File}, source {SourceId}] – {Status}, Dolby Vision profile {Profile}",
                                item.Name, Path.GetFileName(path), id, cached.Status, cached.Profile);
                            continue;
                        }
                        log.LogInformation("Blu-ray ISO probing: {Title} [{File}, source {SourceId}]", item.Name, Path.GetFileName(path), id);
                        var solePlaylist = await tools.ReadSingleDiscPlaylist(path, cancellationToken).ConfigureAwait(false);
                        var result = await DiscProbe.Inspect(encoder.ProbePath, path, solePlaylist, cancellationToken).ConfigureAwait(false);
                        if (DiscCache.Stamp.Read(path) != stamp)
                        {
                            log.LogWarning("Blu-ray ISO changed during probe: {Title} [{File}, source {SourceId}]", item.Name, Path.GetFileName(path), id);
                            continue;
                        }
                        await cache.Write(id, stamp, result, cancellationToken).ConfigureAwait(false);
                        log.LogInformation("Blu-ray ISO result: {Title} [{File}, source {SourceId}] – {Status}, Dolby Vision profile {Profile}",
                            item.Name, Path.GetFileName(path), id, result.Status, result.Profile);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Blu-ray ISO probe failed: {Title} [{File}, source {SourceId}]", item.Name, Path.GetFileName(path), id);
                        try
                        {
                            if (stamp is not null && DiscCache.Stamp.ForProbe(path) == stamp)
                                await cache.Write(id, stamp, new("failed"), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception saveError) when (saveError is not OperationCanceledException)
                        { log.LogWarning(saveError, "Could not store Blu-ray ISO failure for source {SourceId}", id); }
                    }
                }
                progress.Report(Math.Min(99, 100.0 * ++done / Math.Max(1, total)));
            }
            query.StartIndex = done;
        }
        progress.Report(100);
    }
}

internal sealed class DiscCache(string dataPath)
{
    // v1 positives never checked whether Jellyfin and mReel played the same playlist.
    private readonly string _folder = Path.Combine(dataPath, "mafty-disc-vision-v2");
    internal sealed record Stamp(string Path, long Size, long Modified)
    {
        internal bool IsAvailable => Size >= 0;

        internal static Stamp ForProbe(string path) => Read(path) ?? new(path, -1, -1);

        internal static Stamp? Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(path, file.Length, file.LastWriteTimeUtc.Ticks) : null;
        }
    }

    private sealed record Entry(Stamp File, DiscProbe.Result Result);

    internal static bool IsIso(string? path) => string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);

    internal async Task<DiscProbe.Result?> Read(Guid id, Stamp stamp, CancellationToken ct)
    {
        try
        {
            await using var input = File.OpenRead(PathFor(id));
            var entry = await JsonSerializer.DeserializeAsync<Entry>(input, cancellationToken: ct).ConfigureAwait(false);
            return entry?.File == stamp ? entry.Result : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException) { return null; }
    }

    internal async Task Write(Guid id, Stamp stamp, DiscProbe.Result result, CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        string path = PathFor(id), temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Entry(stamp, result)), ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".json");
}
