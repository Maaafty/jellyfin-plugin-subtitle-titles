using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal static class DiscProbe
{
    internal sealed record Result(string Status, int? Profile = null, int? VerifiedPlaylist = null);
    private const string BasePid = "0x1011";
    private const string EnhancementPid = "0x1015";

    internal static async Task<Result> Inspect(string ffprobe, string path, string solePlaylist, CancellationToken ct)
    {
        // FFprobe's "usable playlists" list omits candidates Aether can select.
        // Require the actual PLAYLIST directory to have only one candidate, and
        // check that FFprobe's default open (like Jellyfin's) selects that file.
        var name = solePlaylist.AsSpan().Trim();
        if (name.Length != 10 || !name.EndsWith(".mpls", StringComparison.Ordinal)
            || !int.TryParse(name[..5], NumberStyles.None, CultureInfo.InvariantCulture, out int playlist))
            return new("playlist-unconfirmed");

        var input = "bluray:" + path;
        string[] bounds = ["-probesize", "16777216", "-analyzeduration", "5000000"];
        var (output, diagnostics) = await Tool.RunWithDiagnostics(ffprobe,
            ["-hide_banner", "-v", "info", ..bounds, "-show_streams", "-of", "json", input], ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (DiscSubtitleProbe.SelectedPlaylist(diagnostics) != playlist) return new("playlist-unconfirmed");
        using var streams = JsonDocument.Parse(output);
        var video = streams.RootElement.GetProperty("streams");
        if (!video.EnumerateArray().Any(stream => IsHevc(stream, BasePid))) return new("unknown");
        if (!video.EnumerateArray().Any(stream => IsHevc(stream, EnhancementPid))) return new("no-enhancement-layer", VerifiedPlaylist: playlist);

        // Presence of a secondary HEVC stream alone is not proof it carries Dolby Vision.
        // Pin the packet probe to the exact playlist used for the stream probe.
        using var packets = JsonDocument.Parse(await Tool.Run(ffprobe, ["-v", "error", ..bounds,
            "-playlist", playlist.ToString(CultureInfo.InvariantCulture), "-read_intervals", "%+#96",
            "-select_streams", "i:" + EnhancementPid, "-show_packets", "-show_data", "-show_entries", "packet=data", "-of", "json", input], ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false));
        if (packets.RootElement.TryGetProperty("packets", out var found)
            && found.EnumerateArray().Any(packet => packet.TryGetProperty("data", out var data) && HasRpu(data.GetString() ?? "")))
            return new("confirmed", 7, playlist);
        return new("unknown");
    }

    private static bool IsHevc(JsonElement stream, string pid) =>
        stream.TryGetProperty("id", out var id) && string.Equals(id.GetString(), pid, StringComparison.OrdinalIgnoreCase)
        && stream.TryGetProperty("codec_name", out var codec) && codec.GetString() == "hevc";

    internal static bool HasRpu(string dump)
    {
        // Keep the six-byte Annex-B signature across rows, ignoring the ASCII column.
        const ulong mask = 0xffffffffffff;
        const ulong rpu = 0x0000017c0119;
        ulong window = 0;
        int count = 0;
        foreach (var row in dump.AsSpan().Split('\n'))
        {
            var line = dump.AsSpan()[row];
            if (line.Length < 12 || !line[8..].StartsWith(": ", StringComparison.Ordinal)) continue;
            var payload = line[10..];
            int ascii = payload.IndexOf("  ", StringComparison.Ordinal);
            if (ascii >= 0) payload = payload[..ascii];
            for (int i = 0; i + 1 < payload.Length;)
            {
                if (payload[i] == ' ') { i++; continue; }
                int high = Hex(payload[i++]), low = Hex(payload[i++]);
                if (high < 0 || low < 0) break;
                window = ((window << 8) | (uint)((high << 4) | low)) & mask;
                if (++count >= 6 && window == rpu) return true;
            }
        }
        return false;
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}
