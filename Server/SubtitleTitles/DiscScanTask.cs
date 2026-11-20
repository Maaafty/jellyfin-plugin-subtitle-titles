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
    public string Name => "Identify Blu-ray editions and Dolby Vision";
    public string Key => "MaftyDiscVision";
    public string Description => "Inspect playlists on local Blu-ray ISO and BDMV sources in the selected libraries without decoding video.";
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
            log.LogWarning("Select media libraries in {PluginName} before running the Blu-ray task", Plugin.DisplayName);
            return;
        }
        var store = new DiscEditionStore(paths.DataPath);
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
                    if (!DiscVolume.IsDisc(source.Path) || !Guid.TryParse(source.Id, out var id) || !seen.Add(id)) continue;
                    var path = source.Path!;
                    try
                    {
                        await store.Scan(encoder.ProbePath, path, id, (playlist, status, profile) =>
                            log.LogInformation("Blu-ray result: {Title} [{File}, source {SourceId}, {Playlist}] – {Status}, Dolby Vision profile {Profile}",
                                item.Name, Path.GetFileName(path), id, playlist, status, profile), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { log.LogWarning(ex, "Blu-ray probe failed: {Title} [{File}, source {SourceId}]", item.Name, Path.GetFileName(path), id); }
                }
                progress.Report(Math.Min(99, 100.0 * ++done / Math.Max(1, total)));
            }
            query.StartIndex = done;
        }
        progress.Report(100);
    }
}

