using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

public sealed class ScanTask(ILibraryManager library, IMediaEncoder encoder, IMediaStreamRepository repository,
    IApplicationPaths paths, ILogger<ScanTask> log) : IScheduledTask
{
    public string Name => "Identify subtitle track titles";
    public string Key => TaskKey;
    public string Description => "Inspect subtitle content and preview, apply or restore track titles.";
    public string Category => "Library";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    internal const string StateDirectory = "mafty-subtitle-titles";
    internal const string TaskKey = "MaftySubtitleTitles";
    private const int BitmapEvidenceVersion = 2;
    private const int PreviousBitmapRecognitionVersion = 3;
    private const int BitmapRecognitionVersion = 4;
    internal sealed record ScanRecord(string Fingerprint, TrackEdit[] Tracks, TitleChange[]? TitleHistory = null,
        TrackIssue[]? Issues = null, string? SourceIssue = null);
    internal sealed record TrackIssue(int Index, string? Codec, string Reason);
    internal sealed record TrackIdentity(string Path, long Size, int Index, string Codec, bool External,
        string? Language, string? TimeBase, bool Default, bool Forced, bool Sdh);
    internal sealed record TitleChange(TrackIdentity Track, string? Original, string? Applied, string? Pending = null, bool HasPending = false);
    internal sealed record TrackEdit(int Index, string Codec, string? Original, string? Proposed, Language Language, AssEffects? Effect, bool Conflict,
        bool Reused = false, string? Previous = null, string? Languages = null, bool BitmapInconclusive = false, int BitmapVersion = 0,
        bool BitmapScriptReviewAttempted = false);
    private sealed record Input(MediaStream Stream, string Path, string Format);
    private static bool IsBitmap(Input input) => IsBitmap(input.Format);
    private static bool IsBitmap(string format) => format is "sup" or "dvd" or "dvb" or "xsub";
    private static bool CurrentBitmapResult(TrackEdit edit, bool refreshMkvInconclusive) => edit.BitmapVersion == BitmapRecognitionVersion
        || edit.BitmapVersion == PreviousBitmapRecognitionVersion
        || (edit.BitmapVersion == BitmapEvidenceVersion && !(refreshMkvInconclusive && edit.BitmapInconclusive))
        // Keep earlier directly inspected multilingual results rather than resampling every such track on upgrade.
        || (edit is { Reused: false, BitmapInconclusive: false } && edit.Languages?.Contains(',') == true);

    internal static bool ShouldReviewBitmapScript(Language detected, string? languages)
    {
        if (detected != Language.Chinese) return false;
        var codes = languages.AsSpan();
        foreach (var range in codes.Split(','))
            if (codes[range].SequenceEqual("zh")) return true;
        return false;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var config = plugin.Configuration;
        var tools = plugin.Tools;
        if (!config.RestoreTitles && (!tools.Available || (config.EnableBitmapOcr && !tools.BitmapAvailable)))
            throw new InvalidOperationException("The bundled subtitle tools are missing or incompatible. Install the Linux x86-64 package.");
        bool bitmapReviewAvailable = !config.RestoreTitles && config.EnableBitmapOcr && tools.BitmapBestModelsAvailable;
        var state = Path.Combine(paths.DataPath, StateDirectory);
        Directory.CreateDirectory(state);
        var libraryIds = library.GetVirtualFolders().Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty).ToHashSet();
        var selected = config.LibraryIds.Distinct().ToArray();
        if (selected.Length == 0 || selected.Any(id => id == Guid.Empty || !libraryIds.Contains(id)))
        {
            log.LogWarning("Configure LibraryIds with explicit Jellyfin library folder IDs before running {Key}", Key);
            return;
        }
        InternalItemsQuery ScanQuery(BaseItemKind[] itemTypes) => new()
        {
            AncestorIds = selected, Recursive = true, IsVirtualItem = false, IncludeItemTypes = itemTypes, Limit = 200
        };
        var query = ScanQuery([BaseItemKind.Movie, BaseItemKind.Episode]);
        int count = library.GetCount(query), done = 0;
        var visited = new HashSet<Guid>();
        var reuse = config.ReuseSeasonSubtitles && !config.RestoreTitles ? new SeasonReuse() : null;
        if (reuse is not null)
            await LoadSeasonEvidence(ScanQuery([BaseItemKind.Episode]), state, config.EnableBitmapOcr, bitmapReviewAvailable,
                reuse, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var page = library.GetItemList(query);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sources = item.GetMediaSources(false);
                var episode = item as Episode;
                foreach (var source in sources)
                {
                    // A version has its own item/database row. Never use the parent item's track indices for it.
                    if (!Guid.TryParse(source.Id, out var id) || source.Path is not { } path
                        || !Path.IsPathFullyQualified(path) || !visited.Add(id)) continue;
                    var number = episode is null ? null : EpisodeNumber.Resolve(episode, path);
                    var title = episode is { ParentIndexNumber: int s } && number is int e
                        ? $"{episode.SeriesName} S{s:D2}E{e:D2} – {episode.Name}" : item.Name;
                    var skipReuse = episode is null ? null : ReuseExclusion(episode, sources.Count, path, number);
                    var season = skipReuse is null && episode is not null ? episode.SeasonId : Guid.Empty;
                    if (!File.Exists(path))
                    {
                        if (Streams(id).Any(stream => stream.Type == MediaStreamType.Subtitle))
                            await RecordSourceIssue("fileUnavailable").ConfigureAwait(false);
                        continue;
                    }
                    var ticks = source.RunTimeTicks ?? (DiscCache.IsIso(path) ? 0 : item.RunTimeTicks ?? 0);
                    try { await Scan(id, title, path, TimeSpan.FromTicks(ticks).TotalSeconds,
                        state, config, tools, bitmapReviewAvailable, season, number.GetValueOrDefault(), reuse, skipReuse, cancellationToken)
                        .ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Subtitle scan failed: {Title} [{File}, source {SourceId}]", title, Path.GetFileName(path), id);
                        await RecordSourceIssue("scanFailed").ConfigureAwait(false);
                    }
                    // Source failures must be visible without overwriting title ownership or recognition evidence.
                    async Task RecordSourceIssue(string issue)
                    {
                        if (config.RestoreTitles) return;
                        var file = Path.Combine(state, id.ToString("N") + ".json");
                        try
                        {
                            var old = await ReadRecord(file, cancellationToken).ConfigureAwait(false);
                            await SaveRecord(file, (old ?? new("", [])) with { SourceIssue = issue }, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        { log.LogWarning(ex, "Could not store subtitle scan status: {Title} [{File}]", title, Path.GetFileName(path)); }
                    }
                }
                progress.Report(Math.Min(99, 100.0 * ++done / Math.Max(1, count)));
            }
            query.StartIndex = done;
        }
        progress.Report(100);
    }

    private IReadOnlyList<MediaStream> Streams(Guid id) => repository.GetMediaStreams(new MediaStreamQuery { ItemId = id });

    private static string? ReuseExclusion(Episode episode, int sourceCount, string path, int? number) => episode switch
    {
        _ when DiscCache.IsIso(path) => "Blu-ray ISO",
        _ when sourceCount != 1 => "multiple media sources",
        { ParentIndexNumber: not > 0 } => "not a regular season",
        { IndexNumberEnd: not null } => "multi-episode file",
        _ when number is not > 0 => "missing valid episode number",
        { SeasonId: var seasonId } when seasonId == Guid.Empty => "missing season ID",
        _ => null,
    };

    private async Task LoadSeasonEvidence(InternalItemsQuery query, string state, bool bitmapEnabled, bool bitmapReviewAvailable,
        SeasonReuse reuse, CancellationToken ct)
    {
        // A later episode's direct cache can veto reuse before an earlier episode is scanned or written.
        var visited = new HashSet<Guid>();
        int offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = library.GetItemList(query);
            if (page.Count == 0) return;
            foreach (var item in page)
            {
                ct.ThrowIfCancellationRequested();
                if (item is not Episode episode) continue;
                var sources = episode.GetMediaSources(false);
                foreach (var source in sources)
                {
                    if (!Guid.TryParse(source.Id, out var id) || source.Path is not { } path
                        || !Path.IsPathFullyQualified(path) || !visited.Add(id)) continue;
                    var number = EpisodeNumber.Resolve(episode, path);
                    if (ReuseExclusion(episode, sources.Count, path, number) is not null) continue;
                    try
                    {
                        var record = await ReadRecord(Path.Combine(state, id.ToString("N") + ".json"), ct).ConfigureAwait(false);
                        if (record is not { SourceIssue: null }
                            || !Array.Exists(record.Tracks, static edit => edit is { Reused: false, Effect: not null, Languages: not null })
                            || !File.Exists(path)
                            || !string.Equals(library.GetItemById(id)?.Path, path, StringComparison.Ordinal)) continue;
                        var rows = Streams(id);
                        var inputs = Inputs(rows, path, iso: false);
                        if (inputs.Length == 0 || !CacheMatches(record, Fingerprint(path, inputs), inputs, path, iso: false)) continue;
                        bool mkv = Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
                        SeasonReuse.Episode? sample = null;
                        foreach (var input in inputs)
                        {
                            var stream = input.Stream;
                            if (stream.IsExternal || (IsBitmap(input) && !bitmapEnabled)
                                || record.Issues?.Any(issue => issue.Index == stream.Index && issue.Codec == stream.Codec) == true) continue;
                            var saved = Prior(record.Tracks, stream);
                            if (BitmapRefreshReason(input, saved, mkv, bitmapReviewAvailable) is not null
                                || DirectCachedEdit(saved, stream) is not { } direct) continue;
                            sample ??= reuse.Begin(episode.SeasonId, number.GetValueOrDefault(), path, rows, record.Tracks);
                            sample.Observe(direct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Leave source issue reporting and title ownership changes to the normal scan.
                        log.LogDebug(ex, "Could not preload subtitle season evidence for {SourceId}", id);
                    }
                }
            }
            offset += page.Count;
            query.StartIndex = offset;
        }
    }

    private async Task Scan(Guid id, string title, string path, double duration, string state, PluginConfiguration config, BundledTools tools,
        bool bitmapReviewAvailable, Guid season, int episodeNumber, SeasonReuse? reuse, string? skipReuse, CancellationToken ct)
    {
        var recordPath = Path.Combine(state, id.ToString("N") + ".json");
        var record = await ReadRecord(recordPath, ct).ConfigureAwait(false);
        var rows = Streams(id);
        var subtitles = rows.Where(row => row.Type == MediaStreamType.Subtitle).ToArray();
        if (subtitles.Length == 0) return;
        if (!string.Equals(library.GetItemById(id)?.Path, path, StringComparison.Ordinal)) return;
        var iso = DiscCache.IsIso(path);
        var inputs = Inputs(subtitles, path, iso);
        var identities = UniqueIdentities(path, subtitles);
        var titleHistory = (record?.TitleHistory ?? []).ToDictionary(change => change.Track);
        if (config.RestoreTitles && record?.TitleHistory is not null)
        {
            Restore(id, title, path, titleHistory, ct);
            return;
        }
        if (iso && !config.EnableBitmapOcr && !config.RestoreTitles)
        {
            await SaveRecord(recordPath, (record ?? new("", [])) with { SourceIssue = "bitmapDisabled" }, ct).ConfigureAwait(false);
            return;
        }
        DiscSubtitleProbe? disc = null;
        if (iso)
        {
            if (inputs.Length == 0)
            {
                if (!config.RestoreTitles)
                    await SaveRecord(recordPath, (record ?? new("", [])) with { SourceIssue = "fileUnavailable" }, ct).ConfigureAwait(false);
                return;
            }
            var stamp = DiscCache.Stamp.Read(path)
                ?? throw new InvalidDataException("Blu-ray ISO file is unavailable");
            disc = await DiscSubtitleProbe.Read(encoder.ProbePath, path, rows, ct).ConfigureAwait(false);
            if (DiscCache.Stamp.Read(path) != stamp)
                throw new InvalidDataException("Blu-ray ISO changed during probe");
        }
        var fingerprint = Fingerprint(path, inputs, disc);
        bool cacheMatches = record is not null && CacheMatches(record, fingerprint, inputs, path, iso);
        // An added sidecar changes the evidence fingerprint. For legacy migration only,
        // first try the old set of cached tracks without the new sidecar.
        bool legacyMatches = record is { TitleHistory: null } && (cacheMatches || (!iso &&
            record.Fingerprint == Fingerprint(path, inputs.Where(input => Find(record.Tracks, input.Stream) is not null).ToArray())));
        if (record is not null && legacyMatches)
        {
            foreach (var stream in subtitles)
            {
                if (Find(record.Tracks, stream) is not { } edit || stream.Title == edit.Original || !Matches(edit, stream.Title)
                    || !identities.TryGetValue(stream.Index, out var key)) continue;
                titleHistory[key] = new(key, edit.Original, stream.Title);
            }
            if (titleHistory.Count != 0)
            {
                record = record with { TitleHistory = titleHistory.Values.ToArray() };
                await SaveRecord(recordPath, record, ct).ConfigureAwait(false);
            }
        }
        if (config.RestoreTitles)
        {
            Restore(id, title, path, titleHistory, ct);
            return;
        }
        var cached = cacheMatches ? record!.Tracks : [];
        var tracks = inputs.Where(input => !IsBitmap(input) || config.EnableBitmapOcr).ToArray();
        var issues = new List<TrackIssue>();
        foreach (var stream in subtitles)
        {
            var format = SubtitleFormat(stream.Codec);
            if (format is null || (iso && (stream.IsExternal || format != "sup"))) AddIssue(stream, "unsupported");
            else if (!inputs.Any(input => ReferenceEquals(input.Stream, stream))) AddIssue(stream, "fileUnavailable");
            else if (!config.EnableBitmapOcr && IsBitmap(format)) AddIssue(stream, "bitmapDisabled");
        }
        var episode = season != Guid.Empty ? reuse?.Begin(season, episodeNumber, path, rows, cached) : null;
        bool mkv = Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        var edits = new List<TrackEdit>(tracks.Length);
        var selected = new List<Input>();
        Dictionary<string, int>? reasons = episode is null ? null : [];
        int reused = 0, cachedCount = 0;
        bool dirty = record is null || !cacheMatches || record.SourceIssue is not null;
        foreach (var input in tracks)
        {
            var stream = input.Stream;
            var saved = Find(cached, stream);
            bool bitmap = IsBitmap(input);
            var bitmapRefresh = BitmapRefreshReason(input, saved, mkv, bitmapReviewAvailable, iso);
            // Keep the old record for restore, but never use its classification to authorize a season inference or write.
            if (bitmapRefresh is not null && saved is not null && !Matches(saved, stream.Title))
            {
                cachedCount++;
                continue;
            }
            if (bitmapRefresh is null && DirectCachedEdit(saved, stream) is { } direct)
            {
                dirty |= direct != saved;
                edits.Add(direct);
                cachedCount++;
                continue;
            }
            var decision = bitmapRefresh is not null
                ? new SeasonReuse.Decision(null, bitmapRefresh)
                : saved is not null && !Matches(saved, stream.Title)
                    ? new SeasonReuse.Decision(null, "title changed outside the plugin")
                    : saved is { Reused: false, Languages: null }
                        ? new SeasonReuse.Decision(null, "language list missing") // Preserve the direct result until reinspection.
                        : saved is null && Owned(stream) is not null
                            ? default // Reinspect, but retain the pre-plugin title independently of evidence.
                            : episode?.Suggest(stream, saved) ?? default;
            if (decision.Edit is { } inference)
            {
                if (bitmap) inference = inference with { BitmapVersion = BitmapRecognitionVersion };
                edits.Add(inference);
                dirty |= inference != saved;
                reused++;
                continue;
            }
            if (reasons is not null && decision.Reason is { } reason)
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            selected.Add(input);
        }
        // Cached evidence was already considered in preflight. Only add newly inspected results below.
        int firstInspectedEdit = edits.Count;
        if (selected.Count > 0)
        {
            dirty = true;
            var work = Directory.CreateTempSubdirectory("mafty-subtitles-").FullName;
            try
            {
                foreach (var input in selected.Where(IsBitmap))
                {
                    if (disc is null && duration <= 0)
                    {
                        log.LogInformation("Bitmap subtitle skipped: {Title} track {Track} – missing playback duration", title, input.Stream.Index);
                        AddIssue(input.Stream, "missingDuration");
                        continue;
                    }
                    var sampleFile = bitmapReviewAvailable ? Path.Combine(work, $"{input.Stream.Index}.ocr-samples") : null;
                    try
                    {
                        var images = disc is null
                            ? await tools.ReadBitmap(input.Path, input.Stream.IsExternal ? -1 : input.Stream.Index,
                                duration, sampleFile, ct).ConfigureAwait(false)
                            : await tools.ReadDiscBitmap(path, disc.Playlist, disc.Pid(input.Stream.Index),
                                duration, sampleFile, ct).ConfigureAwait(false);
                        var (detected, languages) = await Recognition.DetectBitmap(images, tools, work, ct).ConfigureAwait(false);
                        bool attemptedScriptReview = false;
                        if (sampleFile is not null && ShouldReviewBitmapScript(detected, languages))
                        {
                            attemptedScriptReview = true;
                            try
                            {
                                var reviewed = await tools.ReviewBitmap(sampleFile, ct).ConfigureAwait(false);
                                detected = await Recognition.DetectBitmapScript(reviewed, tools, work, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException
                                or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                            {
                                log.LogInformation("Bitmap Chinese script review failed: {Title} track {Track} – {Reason}",
                                    title, input.Stream.Index, ex.Message);
                            }
                        }
                        if (languages.Length == 0)
                        {
                            var prior = Find(cached, input.Stream);
                            var original = Original(input.Stream, prior);
                            edits.Add(new TrackEdit(input.Stream.Index, input.Stream.Codec, original, original,
                                Language.Unknown, AssEffects.None, false, Languages: "", BitmapInconclusive: true,
                                BitmapVersion: BitmapRecognitionVersion));
                            log.LogInformation("Bitmap subtitle unchanged: {Title} track {Track} – sampled evidence insufficient", title, input.Stream.Index);
                        }
                        else AddEdit(input.Stream, detected, AssEffects.None, languages, BitmapRecognitionVersion, attemptedScriptReview);
                    }
                    catch (Exception ex) when (IsScanError(ex))
                    {
                        AddIssue(input.Stream, "scanFailed");
                        log.LogInformation("Bitmap subtitle failed: {Title} track {Track} – {Reason}", title, input.Stream.Index, ex.Message);
                    }
                    finally
                    {
                        if (sampleFile is not null)
                        {
                            try { File.Delete(sampleFile); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                log.LogWarning(ex, "Bitmap sample cleanup failed: {Title} track {Track}", title, input.Stream.Index);
                            }
                        }
                    }
                }
                foreach (var group in selected.Where(input => !IsBitmap(input)).GroupBy(input => input.Path, StringComparer.Ordinal))
                {
                    var args = new List<string> { "-nostdin", "-v", "error", "-i", group.Key };
                    foreach (var input in group)
                    {
                        args.AddRange(["-map", input.Stream.IsExternal ? "0:s:0" : $"0:{input.Stream.Index}",
                            "-c:s", input.Format == "ass" ? "copy" : "srt", "-f", input.Format, Output(work, input)]);
                    }
                    try { await Tool.Run(encoder.EncoderPath, args, ct, TimeSpan.FromHours(2)).ConfigureAwait(false); }
                    catch (Exception ex) when (IsScanError(ex))
                    {
                        foreach (var input in group) AddIssue(input.Stream, "scanFailed");
                        log.LogWarning(ex, "Subtitle extraction failed: {Title} [{File}]", title, Path.GetFileName(group.Key));
                        continue;
                    }
                    foreach (var input in group)
                    {
                        var stream = input.Stream;
                        try
                        {
                            var extracted = Output(work, input);
                            var (cues, effects) = input.Format switch
                            {
                                "ass" => Recognition.Ass(await File.ReadAllLinesAsync(extracted, ct).ConfigureAwait(false)),
                                _ => (Recognition.Srt(await File.ReadAllTextAsync(extracted, ct).ConfigureAwait(false)).ToList(), AssEffects.None),
                            };
                            var (detected, languages) = await Recognition.DetectText(cues, tools, work, ct).ConfigureAwait(false);
                            AddEdit(stream, detected, effects, languages);
                        }
                        catch (Exception ex) when (IsScanError(ex))
                        {
                            AddIssue(stream, "scanFailed");
                            log.LogWarning(ex, "Subtitle recognition failed: {Title} track {Track}", title, stream.Index);
                        }
                    }
                }
            }
            finally { Directory.Delete(work, recursive: true); }
        }
        var current = Streams(id);
        if (!SourceMatches(id, current, path, disc, fingerprint))
        {
            await SaveRecord(recordPath, (record ?? new("", [])) with
            {
                TitleHistory = record?.TitleHistory ?? (titleHistory.Count == 0 ? null : titleHistory.Values.ToArray()),
                SourceIssue = "sourceChanged"
            }, ct).ConfigureAwait(false);
            return;
        }
        var history = edits.Concat(cached.Where(edit => !edits.Any(active => active.Index == edit.Index && active.Codec == edit.Codec)
            && !issues.Any(issue => issue.Index == edit.Index && issue.Codec == edit.Codec && issue.Reason != "bitmapDisabled")))
            .OrderBy(edit => edit.Index).ToArray();
        var changed = config.ApplyTitles ? PrepareWrite(path, edits, current, titleHistory) : 0;
        if (dirty || changed != 0 || !issues.SequenceEqual(record?.Issues ?? []))
            await SaveRecord(recordPath, new(fingerprint, history, titleHistory.Values.ToArray(), issues.ToArray()), ct).ConfigureAwait(false);
        if (changed != 0) repository.SaveMediaStreams(id, current, ct);
        if (episode is not null)
            for (int i = firstInspectedEdit; i < edits.Count; i++)
                if (streamsMatch(edits[i], rows)) episode.Observe(edits[i]);

        if (dirty || changed != 0)
        {
            int suggestions = edits.Count(edit => !edit.BitmapInconclusive && (edit.Previous ?? edit.Original) != edit.Proposed && !edit.Conflict);
            int conflicts = edits.Count(edit => edit.Conflict);
            var explanation = reasons?.Count > 0
                ? string.Join(", ", reasons.Select(reason => $"{reason.Value} {reason.Key}"))
                : selected.Count > 0 && reuse is not null && skipReuse is not null ? skipReuse : "—";
            int limited = edits.Count(edit => edit.Effect == AssEffects.Limited);
            int full = edits.Count(edit => edit.Effect == AssEffects.Full);
            log.LogInformation("Subtitles: {Title} [{File}] – scanned {Scanned}, season reused {Reused}, cached {Cached}; suggestions {Suggestions}, conflicts {Conflicts}, applied {Applied}; limited ASS effects {Limited}, full effects {Full}. Scan reasons: {Reasons}",
                title, Path.GetFileName(path), selected.Count, reused, cachedCount, suggestions, conflicts, changed, limited, full, explanation);
        }

        void AddEdit(MediaStream stream, Language detected, AssEffects effects, string languages, int bitmapVersion = 0,
            bool bitmapScriptReviewAttempted = false)
        {
            var prior = Find(cached, stream);
            var original = Original(stream, prior);
            var proposed = TitleRules.Suggest(original, detected, effects, out var conflict, languages, stream.Language);
            var previous = prior is not null && !Matches(prior, stream.Title)
                ? prior.Previous : PreviousTitle(stream.Title, original, proposed);
            edits.Add(new TrackEdit(stream.Index, stream.Codec, original, proposed, detected, effects, conflict,
                Previous: previous, Languages: languages, BitmapVersion: bitmapVersion, BitmapScriptReviewAttempted: bitmapScriptReviewAttempted));
            if (conflict || (previous ?? original) != proposed)
                log.LogDebug("Subtitle track {Index}: {Title} [{File}, source {SourceId}] – {Original} → {Proposed} (conflict: {Conflict})",
                    stream.Index, title, Path.GetFileName(path), id, previous ?? original, proposed, conflict);
        }

        void AddIssue(MediaStream stream, string reason) => issues.Add(new(stream.Index, stream.Codec, reason));

        TitleChange? Owned(MediaStream stream) =>
            identities.TryGetValue(stream.Index, out var key) && titleHistory.TryGetValue(key, out var change)
                ? change : null;

        string? Original(MediaStream stream, TrackEdit? prior) =>
            Owned(stream) is { } change ? change.Original : prior is null ? stream.Title : prior.Original;

        static bool streamsMatch(TrackEdit edit, IReadOnlyList<MediaStream> streams)
            => streams.Any(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal
                && stream.Index == edit.Index && stream.Codec == edit.Codec && Matches(edit, stream.Title));
    }

    private static bool CacheMatches(ScanRecord record, string fingerprint, Input[] inputs, string path, bool iso)
        => record.Fingerprint == fingerprint || (!iso &&
            record.Fingerprint == Fingerprint(path, inputs.Where(input => !IsBitmap(input) || input.Format == "sup").ToArray()));

    private static string? BitmapRefreshReason(Input input, TrackEdit? saved, bool mkv, bool bitmapReviewAvailable, bool iso = false)
    {
        if (!IsBitmap(input) || saved is null) return null;
        // Old ISO windows could land in the wrong clip; raw SUP windows could omit decoder state.
        if (saved.BitmapVersion < BitmapRecognitionVersion && (iso || (input.Stream.IsExternal && input.Format == "sup")))
            return "bitmap sampling corrected";
        // Selectively resample unresolved Chinese results when the script-review models become available.
        if (bitmapReviewAvailable && saved is { Reused: false, BitmapInconclusive: false, BitmapScriptReviewAttempted: false }
            && ShouldReviewBitmapScript(saved.Language, saved.Languages)) return "Chinese script review pending";
        return CurrentBitmapResult(saved, mkv && !input.Stream.IsExternal) ? null : "bitmap evidence version changed";
    }

    private static TrackEdit? DirectCachedEdit(TrackEdit? saved, MediaStream stream)
    {
        if (saved is not { Reused: false, Effect: { } effect, Languages: { } languages }) return null;
        // An inconclusive bitmap sample leaves the title alone, as does a title manually edited since the scan.
        if (saved.BitmapInconclusive || !Matches(saved, stream.Title)) return saved;
        var proposed = TitleRules.Suggest(saved.Original, saved.Language, effect, out var conflict, languages, stream.Language);
        var previous = PreviousTitle(stream.Title, saved.Original, proposed);
        return proposed == saved.Proposed && conflict == saved.Conflict && previous == saved.Previous
            ? saved : saved with { Proposed = proposed, Conflict = conflict, Previous = previous };
    }

    private static TrackEdit? Find(TrackEdit[] cached, MediaStream stream)
        => Array.Find(cached, edit => edit.Index == stream.Index && edit.Codec == stream.Codec);

    private static bool Matches(TrackEdit edit, string? title)
        => title == edit.Original || title == edit.Proposed || (edit.Previous is not null && title == edit.Previous);

    internal static TrackEdit? Prior(TrackEdit[] cached, MediaStream stream)
        => Find(cached, stream) is { } edit && Matches(edit, stream.Title) ? edit : null;

    internal static string? PreviousTitle(string? current, string? original, string? proposed)
        => current != original && current != proposed ? current : null;

    private static async Task<ScanRecord?> ReadRecord(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ScanRecord>(file, cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool IsScanError(Exception ex) => ex is IOException or InvalidDataException
        or TimeoutException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private static async Task SaveRecord(string path, ScanRecord record, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    // External streams can be renumbered when a sidecar is added. Require a unique file/track match instead.
    private static TrackIdentity? Identify(string path, MediaStream stream, Dictionary<string, long?> lengths)
    {
        var filePath = stream.IsExternal ? ExternalPath(stream) : path;
        if (filePath is null || stream.Codec is not { Length: > 0 } codec || stream.Index < 0) return null;
        if (!lengths.TryGetValue(filePath, out var size))
        {
            var file = new FileInfo(filePath);
            lengths.Add(filePath, size = file.Exists ? file.Length : null);
        }
        return size is { } bytes ? new(filePath, bytes, stream.IsExternal ? -1 : stream.Index,
            codec.ToLowerInvariant(), stream.IsExternal, stream.Language, stream.TimeBase,
            stream.IsDefault, stream.IsForced, stream.IsHearingImpaired == true) : null;
    }

    internal static bool OwnsTitle(TitleChange change, string? title) =>
        title != change.Original && (title == change.Applied || (change.HasPending && title == change.Pending));

    internal static Dictionary<int, TrackIdentity> UniqueIdentities(string path, IEnumerable<MediaStream> streams)
    {
        Dictionary<string, long?> lengths = new(StringComparer.Ordinal);
        return streams.Where(stream => stream.Type == MediaStreamType.Subtitle)
            .Select(stream => (stream.Index, Key: Identify(path, stream, lengths)))
            .Where(entry => entry.Key is not null).GroupBy(entry => entry.Key!)
            .Where(group => group.Count() == 1).Select(group => group.Single())
            .GroupBy(entry => entry.Index).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Key!);
    }

    private static int PrepareWrite(string path, IReadOnlyList<TrackEdit> edits, IReadOnlyList<MediaStream> current,
        Dictionary<TrackIdentity, TitleChange> history)
    {
        if (edits.Count == 0) return 0;
        var keys = UniqueIdentities(path, current);
        int changed = 0;
        foreach (var edit in edits)
        {
            if (edit.Effect is null || edit.BitmapInconclusive || (edit.Conflict && edit.Original != edit.Proposed)
                || !keys.TryGetValue(edit.Index, out var key)) continue;
            var track = current.FirstOrDefault(stream => stream.Index == edit.Index && stream.Type == MediaStreamType.Subtitle
                && stream.Codec == edit.Codec);
            if (track is null || track.Title == edit.Proposed) continue;
            var known = history.GetValueOrDefault(key);
            if (known is not null && known.Original != edit.Original) continue;
            if (track.Title != edit.Original && (known is null || !OwnsTitle(known, track.Title))) continue;
            // Persist the transition before touching Jellyfin. After an interruption either title can still be recovered.
            history[key] = new(key, edit.Original, track.Title, edit.Proposed, HasPending: true);
            track.Title = edit.Proposed;
            changed++;
        }
        return changed;
    }

    private void Restore(Guid id, string title, string path, Dictionary<TrackIdentity, TitleChange> history, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (history.Count == 0 || library.GetItemById(id)?.Path != path) return;
        var current = Streams(id);
        var keys = UniqueIdentities(path, current);
        int changed = 0;
        foreach (var stream in current)
        {
            if (stream.Type != MediaStreamType.Subtitle || !keys.TryGetValue(stream.Index, out var key)
                || !history.TryGetValue(key, out var change) || !OwnsTitle(change, stream.Title)) continue;
            stream.Title = change.Original;
            changed++;
        }
        if (changed == 0) return;
        repository.SaveMediaStreams(id, current, ct);
        log.LogInformation("Subtitles restored: {Title} [{File}] – {TrackCount} tracks", title, Path.GetFileName(path), changed);
    }

    private static Input[] Inputs(IReadOnlyList<MediaStream> streams, string path, bool iso) => streams
        .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.Index >= 0 && (!iso || !stream.IsExternal))
        .Select(stream => (Stream: stream, Path: stream.IsExternal ? ExternalPath(stream) : path, Format: SubtitleFormat(stream.Codec)))
        .Where(row => row.Path is not null && (iso ? row.Format == "sup" : row.Format is not null) && File.Exists(row.Path))
        .Select(row => new Input(row.Stream, row.Path!, row.Format!)).OrderBy(row => row.Stream.Index).ToArray();

    internal static string? SubtitleFormat(string? codec) => codec?.ToLowerInvariant() switch
    {
        "ass" or "ssa" => "ass", "pgssub" or "hdmv_pgs_subtitle" or "pgs" => "sup",
        "dvdsub" or "dvd_subtitle" or "vobsub" => "dvd", "dvbsub" or "dvb_subtitle" => "dvb", "xsub" => "xsub",
        "subrip" or "srt" or "webvtt" or "mov_text" or "text" => "srt", _ => null,
    };

    private static string? ExternalPath(MediaStream stream)
        => stream.Path is { } path && stream.Codec?.ToLowerInvariant() is ("dvdsub" or "dvd_subtitle" or "vobsub")
            && Path.GetExtension(path).Equals(".sub", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, ".idx") : stream.Path;

    private bool SourceMatches(Guid id, IReadOnlyList<MediaStream> rows, string path, DiscSubtitleProbe? disc, string fingerprint)
        => (disc is null || (string.Equals(library.GetItemById(id)?.Path, path, StringComparison.Ordinal) && disc.MatchesRows(rows)))
            && Fingerprint(path, Inputs(rows, path, disc is not null), disc) == fingerprint;

    private static string Fingerprint(string movie, Input[] inputs, DiscSubtitleProbe? disc = null)
    {
        var text = new StringBuilder();
        foreach (var path in inputs.SelectMany(input => input.Stream.IsExternal && input.Format == "dvd"
                     ? new[] { input.Path, Path.ChangeExtension(input.Path, ".sub") } : [input.Path])
                 .Prepend(movie).Distinct(StringComparer.Ordinal))
        {
            var file = new FileInfo(path);
            if (!file.Exists) { text.AppendLine(path); continue; }
            text.AppendLine($"{path}\t{file.Length}\t{file.LastWriteTimeUtc.Ticks}");
        }
        foreach (var input in inputs) text.AppendLine($"{input.Stream.Index}\t{input.Stream.Codec}\t{input.Path}");
        if (disc is not null)
        {
            text.AppendLine($"MPLS\t{disc.Playlist}");
            foreach (var input in inputs) text.AppendLine($"{input.Stream.Index}\t{disc.Pid(input.Stream.Index)}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Output(string work, Input input) => Path.Combine(work, $"{input.Stream.Index}.{input.Format}");
}
