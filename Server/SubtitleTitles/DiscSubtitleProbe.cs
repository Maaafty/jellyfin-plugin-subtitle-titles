using System.Globalization;
using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal sealed class DiscSubtitleProbe
{
    private readonly record struct Signature(string Type, string Codec, string TimeBase, string Language,
        bool Default, bool Forced, bool HearingImpaired);
    private readonly record struct Stream(Signature Signature, int Pid);
    private readonly Dictionary<int, Stream> _streams = [];

    private DiscSubtitleProbe(int playlist) => Playlist = playlist;

    internal int Playlist { get; }
    internal int Pid(int index) => _streams[index].Pid;

    internal static async Task<DiscSubtitleProbe> Read(string ffprobe, string path, IReadOnlyList<MediaStream> rows, CancellationToken ct)
    {
        // Use the same default bluray: input as Jellyfin. Capture the resolved playlist so the
        // native reader does not independently pick a title (e.g. when durations tie).
        var (output, diagnostics) = await Tool.RunWithDiagnostics(ffprobe,
            ["-hide_banner", "-v", "info", "-probesize", "16777216", "-analyzeduration", "5000000",
                "-skip_estimate_duration_from_pts", "1", "-show_entries",
                "format=format_name:stream=index,codec_type,codec_name,time_base,id:stream_tags=language:stream_disposition=default,forced,hearing_impaired",
                "-of", "json", "bluray:" + path], ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return Parse(output, diagnostics, rows);
    }

    internal static DiscSubtitleProbe Parse(string output, string diagnostics, IReadOnlyList<MediaStream> rows)
    {
        int playlist = SelectedPlaylist(diagnostics)
            ?? throw new InvalidDataException("ffprobe did not identify an unambiguous Blu-ray playlist");

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        if (!root.TryGetProperty("format", out var format) || Text(format, "format_name") != "mpegts"
            || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("selected Blu-ray playlist is not MPEG-TS");

        var result = new DiscSubtitleProbe(playlist);
        var pids = new HashSet<int>();
        var sharedPids = new HashSet<int>();
        foreach (var stream in streams.EnumerateArray())
        {
            var type = Text(stream, "codec_type");
            var codec = Codec(type, Text(stream, "codec_name"));
            var tags = stream.TryGetProperty("tags", out var t) ? t : default;
            var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
            var signature = new Signature(type, codec, Text(stream, "time_base"), Text(tags, "language").ToLowerInvariant(),
                Flag(disposition, "default"), Flag(disposition, "forced"), Flag(disposition, "hearing_impaired"));
            var id = Text(stream, "id");
            if (!stream.TryGetProperty("index", out var index) || !index.TryGetInt32(out int number) || number < 0
                || !id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(id.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int pid)
                || pid is < 0x20 or >= 0x1fff || !result._streams.TryAdd(number, new(signature, pid)))
                throw new InvalidDataException("ffprobe returned an invalid Blu-ray stream index or PID");
            if (!pids.Add(pid)) sharedPids.Add(pid);
        }

        if (!result.MatchesRows(rows))
            throw new InvalidDataException("ffprobe stream signature does not match the Jellyfin source rows");
        // TrueHD and its AC-3 core can legitimately share an audio PID. A PGS PID must be unique.
        if (result._streams.Values.Any(stream => stream.Signature is { Type: "subtitle", Codec: "pgssub" } && sharedPids.Contains(stream.Pid)))
            throw new InvalidDataException("ffprobe returned a shared Blu-ray PGS PID");
        return result;
    }

    internal static int? SelectedPlaylist(string diagnostics)
    {
        int playlist = -1;
        foreach (var range in diagnostics.AsSpan().Split('\n'))
        {
            var line = diagnostics.AsSpan()[range].TrimEnd();
            if (!line.StartsWith("[bluray @ ", StringComparison.Ordinal)) continue;
            int prefix = line.IndexOf("] selected ", StringComparison.Ordinal);
            if (prefix < 0) continue;
            var selection = line[(prefix + 11)..];
            if (playlist >= 0 || selection.Length != 10 || !selection.EndsWith(".mpls", StringComparison.Ordinal)
                || !int.TryParse(selection[..5], NumberStyles.None, CultureInfo.InvariantCulture, out playlist))
                return null;
        }
        return playlist >= 0 ? playlist : null;
    }

    internal bool MatchesRows(IReadOnlyList<MediaStream> rows)
    {
        var indices = new HashSet<int>();
        int internalCount = 0;
        foreach (var row in rows)
        {
            if (!indices.Add(row.Index)) return false;
            if (row.IsExternal) continue;
            internalCount++;
            var type = row.Type switch
            {
                MediaStreamType.Video => "video", MediaStreamType.Audio => "audio",
                MediaStreamType.Subtitle => "subtitle", MediaStreamType.Data => "data", _ => "",
            };
            if (type.Length == 0 || !_streams.TryGetValue(row.Index, out var stream)
                || stream.Signature != new Signature(type, Codec(type, row.Codec), row.TimeBase ?? "", row.Language?.ToLowerInvariant() ?? "",
                    row.IsDefault, row.IsForced, row.IsHearingImpaired == true))
                return false;
        }
        return internalCount == _streams.Count;
    }

    private static string Codec(string type, string? value)
    {
        var codec = value?.ToLowerInvariant() ?? "";
        return type != "subtitle" ? codec : codec switch
        {
            "hdmv_pgs_subtitle" or "pgs" => "pgssub", "dvd_subtitle" => "dvdsub", "dvb_subtitle" => "dvbsub",
            _ => codec,
        };
    }

    private static string Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var entry) && entry.ValueKind == JsonValueKind.String ? entry.GetString()! : "";

    private static bool Flag(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var entry) && entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out int flag) && flag == 1;
}
