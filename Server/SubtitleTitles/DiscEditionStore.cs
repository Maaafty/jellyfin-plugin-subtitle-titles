using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

public sealed record DiscEdition(string Playlist, string? Name, string Evidence, double DurationSeconds, string ContentGroup,
    string Status, int? DolbyVisionProfile);
public sealed record DiscEditionCatalog(DiscEdition[] Editions);

internal sealed class DiscEditionStore(string dataPath)
{
    private readonly string _folder = Path.Combine(dataPath, "mafty-disc-editions-v1");
    private sealed record Probe(string Fingerprint, DiscProbe.Result Result);

    internal async Task<DiscEditionCatalog> Describe(string path, Guid id, CancellationToken ct)
    {
        using var volume = DiscVolume.OpenVolume(path);
        if (volume is null) return new([]);
        var records = await Read(id, ct).ConfigureAwait(false);
        var editions = DiscNavigator.Read(volume, ct);
        return new(editions.Select(edition =>
        {
            var stamp = Fingerprint(path, edition);
            var result = stamp is not null && records.TryGetValue(edition.Playlist, out var value) && value.Fingerprint == stamp
                ? value.Result : null;
            return new DiscEdition(edition.Playlist, edition.Name, edition.Evidence, edition.DurationSeconds, edition.ContentGroup,
                result?.Status ?? "not-scanned", result?.Profile);
        }).ToArray());
    }

    internal async Task Scan(string ffprobe, string path, Guid id, Action<string, string, int?> report, CancellationToken ct)
    {
        using var volume = DiscVolume.OpenVolume(path);
        if (volume is null) { report("—", "file-unavailable", null); return; }
        var editions = DiscNavigator.Read(volume, ct);
        var records = await Read(id, ct).ConfigureAwait(false);
        // Probe every authored edition; when none can be identified, do not probe every candidate.
        var selected = editions.Where(edition => edition.Evidence != "candidate").ToArray();
        if (selected.Length == 0) selected = editions.Take(1).ToArray();
        bool changed = false;
        foreach (var edition in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryPlaylist(edition.Playlist, out int playlist) || Fingerprint(path, edition) is not { } stamp) continue;
            if (records.TryGetValue(edition.Playlist, out var cached) && cached.Fingerprint == stamp && cached.Result.Status != "failed")
            {
                report(edition.Playlist, cached.Result.Status, cached.Result.Profile);
                continue;
            }
            DiscProbe.Result result;
            try
            {
                result = await DiscProbe.InspectPlaylist(ffprobe, DiscVolume.FolderRoot(path) ?? path, playlist, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or TimeoutException or System.ComponentModel.Win32Exception)
            { result = new("failed"); }
            if (Fingerprint(path, edition) != stamp)
            {
                report(edition.Playlist, "source-changed", null);
                continue;
            }
            records[edition.Playlist] = new(stamp, result);
            changed = true;
            report(edition.Playlist, result.Status, result.Profile);
        }
        if (!changed) return;
        Directory.CreateDirectory(_folder);
        var file = PathFor(id);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(records), ct).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private async Task<Dictionary<string, Probe>> Read(Guid id, CancellationToken ct)
    {
        try
        {
            await using var input = File.OpenRead(PathFor(id));
            return await JsonSerializer.DeserializeAsync<Dictionary<string, Probe>>(input, cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException) { return []; }
    }

    private string PathFor(Guid id) => Path.Combine(_folder, id.ToString("N") + ".json");

    private static string? Fingerprint(string path, DiscNavigator.Edition edition)
    {
        if (DiscVolume.FolderRoot(path) is not { } root)
        {
            var stamp = DiscVolume.Stamp.Read(path);
            return stamp is null ? null : FormattableString.Invariant($"{stamp.Path}\n{stamp.Size}\n{stamp.Modified}");
        }
        var fields = new StringBuilder(root).Append('\n');
        if (!Append(Path.Combine(root, "BDMV", "PLAYLIST", edition.Playlist))) return null;
        foreach (var clip in edition.Clips.Distinct(StringComparer.Ordinal))
            if (!Append(Path.Combine(root, "BDMV", "STREAM", clip + ".m2ts"))) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields.ToString())));

        bool Append(string file)
        {
            if (DiscVolume.Stamp.Read(file) is not { } stamp) return false;
            fields.Append(stamp.Path).Append('\n').Append(stamp.Size).Append('\n').Append(stamp.Modified).Append('\n');
            return true;
        }
    }

    internal static bool TryPlaylist(string name, out int number)
    {
        number = 0;
        return name.Length == 10 && name.EndsWith(".mpls", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(0, 5), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
