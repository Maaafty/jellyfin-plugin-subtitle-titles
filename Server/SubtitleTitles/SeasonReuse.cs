using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal sealed class SeasonReuse
{
    private sealed record Track(string Codec, string? Language, string? Title, bool Default, bool Forced, bool Sdh)
    {
        internal static Track From(MediaStream stream, string? title) => new(stream.Codec?.ToLowerInvariant() ?? "",
            stream.Language?.ToLowerInvariant(), title?.Trim(), stream.IsDefault, stream.IsForced, stream.IsHearingImpaired == true);
    }
    private sealed record Evidence(Language Language, AssEffects Effect, string Languages);
    private sealed class Samples
    {
        internal Evidence? Result;
        internal readonly HashSet<int> Episodes = [];
        internal string? BlockedReason;
    }

    internal readonly record struct Decision(ScanTask.TrackEdit? Edit, string? Reason);
    private readonly Dictionary<(Guid Season, string? Folder, string Container, Track Track), Samples> _samples = [];

    internal Episode Begin(Guid season, int number, string path, IReadOnlyList<MediaStream> streams, ScanTask.TrackEdit[] cached)
        => new(this, season, number, path, streams, cached);

    internal sealed class Episode
    {
        private readonly SeasonReuse _owner;
        private readonly Guid _season;
        private readonly int _number;
        private readonly string? _folder;
        private readonly string _container;
        private readonly Dictionary<int, Track> _tracks = [];
        private readonly HashSet<Track> _duplicates = [];

        internal Episode(SeasonReuse owner, Guid season, int number, string path, IReadOnlyList<MediaStream> streams, ScanTask.TrackEdit[] cached)
        {
            _owner = owner;
            _season = season;
            _number = number;
            _folder = Path.GetDirectoryName(path);
            _container = Path.GetExtension(path).ToLowerInvariant();
            var distinct = new HashSet<Track>();
            foreach (var stream in streams)
            {
                if (stream.Type != MediaStreamType.Subtitle || stream.IsExternal) continue;
                var prior = ScanTask.Prior(cached, stream);
                var track = Track.From(stream, prior is null ? stream.Title : prior.Original);
                _tracks.Add(stream.Index, track);
                if (!distinct.Add(track)) _duplicates.Add(track);
            }
        }

        internal Decision Suggest(MediaStream stream, ScanTask.TrackEdit? prior)
        {
            if (stream.IsExternal) return new(null, "external track");
            var track = _tracks[stream.Index];
            if (_duplicates.Contains(track)) return new(null, "indistinguishable tracks");
            if (!_owner._samples.TryGetValue((_season, _folder, _container, track), out var samples))
                return new(null, "no matching sample");
            if (samples.BlockedReason is { } reason) return new(null, reason);
            if (samples.Episodes.Count < 2) return new(null, "only one matching episode");
            var evidence = samples.Result!;
            var original = prior is null ? stream.Title : prior.Original;
            var proposed = TitleRules.Suggest(original, evidence.Language, evidence.Effect, out var conflict, evidence.Languages, stream.Language);
            if (conflict) return new(null, "language conflict");
            // Never change the conclusion for a previously written inference without reading the actual track.
            if (prior is not null
                && (prior.Language != evidence.Language || (prior.Effect is { } effect && effect != evidence.Effect)
                    || (prior.Languages is not null && prior.Languages != evidence.Languages)))
                return new(null, "previous inference differs");
            return new(new ScanTask.TrackEdit(stream.Index, stream.Codec, original, proposed, evidence.Language,
                evidence.Effect, false, Reused: true, Previous: ScanTask.PreviousTitle(stream.Title, original, proposed), Languages: evidence.Languages), null);
        }

        internal void Observe(ScanTask.TrackEdit edit)
        {
            if (edit.Reused || edit.Effect is not { } effect || edit.Languages is not { } languages
                || !_tracks.TryGetValue(edit.Index, out var track) || _duplicates.Contains(track)) return;
            var key = (_season, _folder, _container, track);
            if (!_owner._samples.TryGetValue(key, out var samples)) _owner._samples[key] = samples = new Samples();
            if (samples.BlockedReason is not null) return;
            var evidence = new Evidence(edit.Language, effect, languages);
            samples.BlockedReason = edit.Conflict ? "sample has a language conflict"
                : edit.Language == Language.Unknown && languages.Length == 0 ? "sample language is unknown"
                : samples.Result is not null && samples.Result != evidence ? "sample results disagree" : null;
            if (samples.BlockedReason is not null) return;
            samples.Result = evidence;
            samples.Episodes.Add(_number);
        }
    }
}
