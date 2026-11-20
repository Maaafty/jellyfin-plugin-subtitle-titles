using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal static class DiscNavigator
{
    internal sealed record Edition(string Playlist, string? Name, string Evidence, double DurationSeconds,
        string ContentGroup, string[] Clips, int Chapters);

    private const string PlaylistDirectory = "BDMV/PLAYLIST";
    private const string JarDirectory = "BDMV/JAR";
    private const int BinaryLimit = 4 * 1024 * 1024;
    private const int XmlLimit = 1024 * 1024;
    private const int Clock = 45000;

    private sealed record Playlist(string File, long Ticks, string ContentGroup, string[] Clips, int Chapters, bool Repeated);

    private sealed class MetadataBudget(DiscVolume source) : DiscVolume
    {
        private long _remaining = 24 * 1024 * 1024;
        internal bool Exhausted { get; private set; }
        internal override string[] List(string directory)
        {
            var files = source.List(directory);
            if (files.Length > 4096) Exhausted = true;
            return files;
        }

        internal override Stream? Open(string path)
        {
            if (Exhausted) return null;
            var file = source.Open(path);
            if (file is null) return null;
            if (file.Length is > 0 and <= BinaryLimit)
            {
                _remaining -= file.Length;
                if (_remaining >= 0) return file;
                Exhausted = true;
            }
            file.Dispose();
            return null;
        }

        public override void Dispose() { } // Caller owns the volume.
    }

    internal static Edition[] Read(DiscVolume volume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var metadata = new MetadataBudget(volume);
        volume = metadata;
        var streams = new Dictionary<int, string>();
        foreach (string file in volume.List("BDMV/STREAM"))
            if (FileId(file, ".m2ts") is int id) streams.TryAdd(id, file[..5]);
        if (streams.Count == 0) return [];

        var playlists = new Dictionary<int, Playlist>();
        foreach (string file in volume.List(PlaylistDirectory).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (FileId(file, ".mpls") is not int id || playlists.ContainsKey(id)) continue;
            if (ParsePlaylist(file, ReadFile(volume, PlaylistDirectory + "/" + file, BinaryLimit, ct), streams, ct) is { } playlist)
                playlists.Add(id, playlist);
        }
        if (playlists.Count == 0) return [];

        var authored = new Dictionary<int, string>();
        var menus = new HashSet<int>();
        ReadAuthoring(volume, authored, menus, ct);
        var navigation = ReadNavigation(volume, menus, ct);
        if (metadata.Exhausted) return [];
        var editions = new List<Edition>();
        var evidencedContent = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, Playlist>(StringComparer.Ordinal);
        long navigationMinimum = playlists.Any(entry => !entry.Value.Repeated && !menus.Contains(entry.Key) && entry.Value.Ticks >= 30 * 60 * Clock)
            ? 30L * 60 * Clock : 5L * 60 * Clock;
        foreach ((int id, Playlist playlist) in playlists)
        {
            ct.ThrowIfCancellationRequested();
            if (authored.TryGetValue(id, out string? name))
            {
                editions.Add(ToEdition(playlist, name, "authored"));
                evidencedContent.Add(playlist.ContentGroup);
            }
            else if (navigation.Contains(id) && !menus.Contains(id) && !playlist.Repeated && playlist.Ticks >= navigationMinimum)
            {
                editions.Add(ToEdition(playlist, null, "navigation"));
                evidencedContent.Add(playlist.ContentGroup);
            }
            else if (!menus.Contains(id) && !playlist.Repeated && playlist.Ticks >= 5 * 60 * Clock
                && (!candidates.TryGetValue(playlist.ContentGroup, out var previous) || playlist.Chapters > previous.Chapters))
            {
                // Chapters can break a tie between unlabelled copies, but don't make a new cut.
                candidates[playlist.ContentGroup] = playlist;
            }
        }

        // Prefer feature-length candidates. On discs without one, keep the plausible shorter choices.
        long minimum = editions.Count != 0 || candidates.Values.Any(p => p.Ticks >= 30 * 60 * Clock)
            ? 30 * 60 * Clock : 5 * 60 * Clock;
        foreach (var playlist in candidates.Values)
            if (playlist.Ticks >= minimum && !evidencedContent.Contains(playlist.ContentGroup))
                editions.Add(ToEdition(playlist, null, "candidate"));

        return editions.OrderBy(e => e.Evidence switch { "authored" => 0, "navigation" => 1, _ => 2 })
            .ThenBy(e => e.Name is not null && IsPrimaryFeature(e.Name) ? 0 : 1)
            .ThenByDescending(e => e.DurationSeconds)
            .ThenBy(e => e.Playlist, StringComparer.Ordinal)
            .ToArray();
    }

    internal static Edition? ReadPlaylist(DiscVolume volume, string playlist, CancellationToken ct)
    {
        if (FileId(playlist, ".mpls") is null) return null;
        return ParsePlaylist(playlist, ReadFile(volume, PlaylistDirectory + "/" + playlist, BinaryLimit, ct), null, ct) is { } parsed
            ? ToEdition(parsed, null, "candidate") : null;
    }

    private static Edition ToEdition(Playlist playlist, string? name, string evidence) =>
        new(playlist.File, name, evidence, playlist.Ticks / (double)Clock, playlist.ContentGroup, playlist.Clips, playlist.Chapters);

    private static Playlist? ParsePlaylist(string file, ReadOnlySpan<byte> data, Dictionary<int, string>? streams, CancellationToken ct)
    {
        if (!Header(data, "MPLS"u8) || data.Length < 40) return null;
        uint listAddress = U32(data, 8), markAddress = U32(data, 12), extensionAddress = U32(data, 16);
        if (listAddress < 40 || !Section(data, listAddress, out var list) || list.Length < 6
            || (markAddress != 0 && markAddress < listAddress + 4 + list.Length)
            || (extensionAddress != 0 && extensionAddress < listAddress + 4 + list.Length)) return null;
        int count = U16(list, 2), subpaths = U16(list, 4);
        list = list[6..];
        if (count == 0 || count > list.Length / 22) return null;

        // One fixed-width key per primary play item: disc clip identifier and the on-disc 45 kHz times.
        // Deliberately do not hash marks, angle alternatives or stream tables.
        const int keySize = 13;
        byte[]? rented = null;
        Span<byte> keys = count <= 64 ? stackalloc byte[count * keySize]
            : (rented = ArrayPool<byte>.Shared.Rent(count * keySize)).AsSpan(0, count * keySize);
        try
        {
            var clips = new string[count];
            HashSet<(int, uint, uint)>? intervals = count >= 4 ? [] : null;
            long ticks = 0, repeatedTicks = 0;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!TakeRecord(ref list, out var item) || item.Length < 20 || !TryClip(item, streams, out int id)) return null;
                uint start = U32(item, 12), end = U32(item, 16);
                if (end < start) return null;
                ticks += end - start;
                if (intervals?.Add((id, start, end)) == false) repeatedTicks += end - start;
                clips[i] = streams is null ? Encoding.ASCII.GetString(item[..5]) : streams[id];
                var key = keys.Slice(i * keySize, keySize);
                item[..5].CopyTo(key);
                item.Slice(12, 8).CopyTo(key[5..]);
                if ((item[10] & 0x10) != 0 && (item.Length < 34 || !OtherClips(item[34..], item[32], streams))) return null;
            }
            if (ticks == 0) return null;
            for (int i = 0; i < subpaths; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!Section(list, 0, out var subpath) || subpath.Length < 6) return null;
                list = list[(4 + subpath.Length)..];
                int items = subpath[5];
                subpath = subpath[6..];
                for (int j = 0; j < items; j++)
                {
                    if (!TakeRecord(ref subpath, out var item) || item.Length < 28 || !TryClip(item, streams, out _)) return null;
                    if ((item[12] & 1) != 0 && (item.Length < 29 || !OtherClips(item[29..], item[28], streams))) return null;
                }
            }

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(keys, digest);
            int chapters = CountChapters(data, markAddress, extensionAddress, keys);
            return new(file, ticks, Convert.ToHexString(digest), clips, chapters, repeatedTicks * 2 > ticks);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryClip(ReadOnlySpan<byte> item, Dictionary<int, string>? streams, out int id)
    {
        id = 0;
        foreach (byte digit in item[..5])
        {
            if (digit is < (byte)'0' or > (byte)'9') return false;
            id = id * 10 + digit - '0';
        }
        return (item.Slice(5, 4).SequenceEqual("M2TS"u8) || item.Slice(5, 4).SequenceEqual("FMTS"u8))
            && (streams is null || streams.ContainsKey(id));
    }

    private static bool OtherClips(ReadOnlySpan<byte> data, int count, Dictionary<int, string>? streams)
    {
        if (count < 1 || count - 1 > data.Length / 10) return false;
        for (int i = 0; i < count - 1; i++)
            if (!TryClip(data[(i * 10)..], streams, out _)) return false;
        return true;
    }

    private static int CountChapters(ReadOnlySpan<byte> data, uint address, uint extension, ReadOnlySpan<byte> keys)
    {
        if (address == 0 || !Section(data, address, out var marks) || marks.Length < 2
            || (extension != 0 && extension < address + 4 + marks.Length)) return 0;
        int count = U16(marks, 0), chapters = 0;
        marks = marks[2..];
        if (count > marks.Length / 14) return 0;
        for (int i = 0; i < count; i++)
        {
            var mark = marks[(i * 14)..];
            int reference = U16(mark, 2);
            if (mark[1] != 1 || reference >= keys.Length / 13) continue;
            var key = keys[(reference * 13)..];
            uint time = U32(mark, 4);
            if (time >= U32(key, 5) && time <= U32(key, 9)) chapters++;
        }
        return chapters;
    }

    private static void ReadAuthoring(DiscVolume volume, Dictionary<int, string> authored, HashSet<int> menus, CancellationToken ct)
    {
        foreach (string directory in volume.List(JarDirectory).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (directory.Length != 5 || DecimalId(directory) is null) continue;
            string path = JarDirectory + "/" + directory;
            foreach (string file in volume.List(path).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (file.Equals("playlists.xml", StringComparison.OrdinalIgnoreCase))
                    ReadSony(ReadXml(volume, path + "/" + file, ct), authored);
                else if (file.Equals("bluray_project.bin", StringComparison.OrdinalIgnoreCase))
                    ReadProject(ReadFile(volume, path + "/" + file, BinaryLimit, ct), authored, menus, ct);
                else if (file.Equals("playbackconfig.xml", StringComparison.OrdinalIgnoreCase))
                    ReadPlaybackConfig(ReadXml(volume, path + "/" + file, ct), authored, menus);
            }
        }
    }

    private static void ReadSony(XElement? root, Dictionary<int, string> authored)
    {
        if (root?.Name.LocalName != "playlist-metadata") return;
        foreach (var playlist in root.Elements())
        {
            if (playlist.Name.LocalName != "playlist" || (string?)playlist.Attribute("name") is not string name
                || (string?)playlist.Attribute("id") is not string value || DecimalId(value) is not int id) continue;
            if (IsFeature(name.Trim().TrimStart('_'), "Feature")) AddFeature(authored, id, name.Trim());
        }
    }

    private static void ReadPlaybackConfig(XElement? root, Dictionary<int, string> authored, HashSet<int> menus)
    {
        if (root?.Name.LocalName == "Project") root = root.Element(root.Name.Namespace + "Discs");
        if (root?.Name.LocalName != "Discs") return;
        foreach (var entry in root.Elements())
        {
            if (entry.Name.LocalName is not ("FeaturePLs" or "aTopMenuPL")
                || ChildValue(entry, "Name") is not string name || ChildValue(entry, "playlistID") is not string value
                || DecimalId(value) is not int id) continue;
            if (entry.Name.LocalName == "aTopMenuPL") menus.Add(id);
            else if (IsProjectFeature(name)) AddFeature(authored, id, name);
        }
    }

    private static string? ChildValue(XElement parent, string name)
    {
        XElement? found = null;
        foreach (var element in parent.Elements())
        {
            if (element.Name.LocalName != name) continue;
            if (found is not null || element.HasElements) return null;
            found = element;
        }
        return found?.Value.Trim();
    }

    private static void ReadProject(ReadOnlySpan<byte> data, Dictionary<int, string> authored, HashSet<int> menus, CancellationToken ct)
    {
        // Verify the initial project name and each individual record. The intervening project
        // metadata varies between discs and need not be a standalone length-prefixed name.
        if (!TakeRecord(ref data, out var project) || project.IsEmpty
            || project.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0) return;
        while (data.Length >= 2 + 4 + 6)
        {
            ct.ThrowIfCancellationRequested();
            int feature = data.IndexOf("FPL_Main"u8), menu = data.IndexOf("TMPL"u8);
            int nameStart = feature < 0 ? menu : menu < 0 ? feature : Math.Min(feature, menu);
            if (nameStart < 0) break;
            if (nameStart >= 2)
            {
                int length = U16(data, nameStart - 2), end = nameStart + length;
                if (length is >= 4 and <= 96 && end <= data.Length - 8 && U32(data, end) == 0)
                {
                    int id = U16(data, end + 4);
                    var label = data.Slice(nameStart, length);
                    if (label.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) < 0)
                    {
                        string name = Encoding.ASCII.GetString(label);
                        if (IsProjectFeature(name)) AddFeature(authored, id, name);
                        else if (name.StartsWith("TMPL Main Menu", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("TMPL_Main Menu", StringComparison.OrdinalIgnoreCase)) menus.Add(id);
                    }
                }
            }
            data = data[(nameStart + 4)..];
        }
    }

    private static bool IsProjectFeature(string name) => IsFeature(name, "FPL_MainFeature") || IsFeature(name, "FPL_Main Feature");

    private static bool IsPrimaryFeature(string name) => name.TrimStart('_').Equals("Feature", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FPL_MainFeature", StringComparison.OrdinalIgnoreCase) || name.Equals("FPL_Main Feature", StringComparison.OrdinalIgnoreCase);

    private static bool IsFeature(string name, string prefix)
    {
        if (name.Length > 96 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var qualifier = name.AsSpan(prefix.Length);
        if (qualifier.IsEmpty) return true;
        if (qualifier[0] is not (' ' or '_' or '-')) return qualifier.Equals("TenET", StringComparison.OrdinalIgnoreCase);
        bool any = false;
        foreach (var tokenRange in qualifier.SplitAny(" _-"))
        {
            var token = qualifier[tokenRange];
            if (token.IsEmpty) continue;
            foreach (char c in token)
                if (!char.IsAsciiLetterOrDigit(c)) return false;
            if (token.Equals("intro", StringComparison.OrdinalIgnoreCase) || token.Equals("outro", StringComparison.OrdinalIgnoreCase)
                || token.Equals("trailer", StringComparison.OrdinalIgnoreCase) || token.Equals("menu", StringComparison.OrdinalIgnoreCase)
                || token.Equals("bonus", StringComparison.OrdinalIgnoreCase) || token.Equals("featurette", StringComparison.OrdinalIgnoreCase)) return false;
            any = true;
        }
        return any;
    }

    private static void AddFeature(Dictionary<int, string> authored, int id, string name)
    {
        if (!authored.TryGetValue(id, out var previous) || (!IsPrimaryFeature(previous) && IsPrimaryFeature(name)))
            authored[id] = name;
    }

    private static HashSet<int> ReadNavigation(DiscVolume volume, HashSet<int> menus, CancellationToken ct)
    {
        ReadOnlySpan<byte> index = ReadFile(volume, "BDMV/index.bdmv", BinaryLimit, ct);
        ReadOnlySpan<byte> objects = ReadFile(volume, "BDMV/MovieObject.bdmv", BinaryLimit, ct);
        var navigation = new HashSet<int>();
        if (!Header(index, "INDX"u8) || index.Length < 40 || U32(index, 8) < 40
            || !Section(index, U32(index, 8), out var titles) || titles.Length < 26
            || !Header(objects, "MOBJ"u8) || !Section(objects, 40, out objects) || objects.Length < 6) return navigation;

        int count = U16(titles, 24), objectCount = U16(objects, 4);
        if (count > (titles.Length - 26) / 12 || objectCount > (objects.Length - 6) / 4) return navigation;
        var direct = new int[objectCount];
        objects = objects[6..];
        for (int i = 0; i < direct.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (objects.Length < 4) return navigation;
            int length = U16(objects, 2) * 12;
            objects = objects[4..];
            if (length > objects.Length) return navigation;
            direct[i] = DirectPlaylist(objects[..length]);
            objects = objects[length..];
        }

        // First Playback, Top Menu and interactive titles can open menu loops. Even when such
        // an object is also exposed as a movie title, don't promote its playlist as navigation.
        var menuObjects = new HashSet<int>();
        for (int i = -2; i < count; i++)
        {
            var title = titles[(i < 0 ? (i + 2) * 12 : 26 + i * 12)..];
            if (title[0] >> 6 != 1) continue; // HDMV, not BD-J
            int obj = U16(title, 6);
            if (obj >= direct.Length || (i >= 0 && title[4] >> 6 != 1)) continue;
            menuObjects.Add(obj);
            if (direct[obj] >= 0) menus.Add(direct[obj]);
        }
        for (int i = 0; i < count; i++)
        {
            var title = titles[(26 + i * 12)..];
            if (title[0] >> 6 != 1 || title[4] >> 6 != 0) continue;
            int obj = U16(title, 6);
            if (obj < direct.Length && !menuObjects.Contains(obj) && direct[obj] >= 0) navigation.Add(direct[obj]);
        }
        return navigation;
    }

    private static int DirectPlaylist(ReadOnlySpan<byte> commands)
    {
        for (; !commands.IsEmpty; commands = commands[12..])
        {
            uint op = U32(commands, 0);
            // A literal first operand identifies the playlist even when the mark/item is a register.
            if (op is 0x22800000 or 0x42810000 or 0x42820000 or 0x42c10000 or 0x42c20000)
                return U32(commands, 4) is <= 99999 and var id ? (int)id : -1;

            // Only a straight-line prefix: NOP, ordinary register updates and SetStream.
            // Comparisons can skip following commands; jumps and dynamic playback are not resolved.
            int group = (commands[0] >> 3) & 3;
            int subgroup = commands[0] & 7;
            if (op != 0 && !(group == 2 && (subgroup == 0 || (subgroup == 1 && commands[3] == 1)))) break;
        }
        return -1;
    }

    private static XElement? ReadXml(DiscVolume volume, string path, CancellationToken ct)
    {
        var data = ReadFile(volume, path, XmlLimit, ct);
        if (data.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = XmlLimit
            });
            return XDocument.Load(xml).Root;
        }
        catch (XmlException) { return null; }
    }

    private static byte[] ReadFile(DiscVolume volume, string path, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = volume.Open(path);
            if (stream is null) return [];
            if (stream.CanSeek)
            {
                long length = stream.Length;
                if (length <= 0 || length > limit) return [];
                var data = new byte[(int)length];
                stream.ReadExactly(data);
                return data;
            }

            using var buffer = new MemoryStream();
            Span<byte> chunk = stackalloc byte[8192];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(chunk);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length + read > limit) return [];
                buffer.Write(chunk[..read]);
            }
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static int? FileId(string file, string extension) =>
        file.Length == 5 + extension.Length && file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? DecimalId(file.AsSpan(0, 5)) : null;

    private static int? DecimalId(ReadOnlySpan<char> value) => value.Length is >= 1 and <= 5
        && value.IndexOfAnyExceptInRange('0', '9') < 0
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;

    private static bool Header(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) => data.Length >= 8
        && data[..4].SequenceEqual(signature) && data.Slice(4, 4).IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;

    private static bool Section(ReadOnlySpan<byte> data, uint address, out ReadOnlySpan<byte> section)
    {
        section = default;
        if (data.Length < 4 || address > (uint)(data.Length - 4)) return false;
        var lengthAndBody = data[(int)address..];
        uint length = U32(lengthAndBody, 0);
        if (length > (uint)(lengthAndBody.Length - 4)) return false;
        section = lengthAndBody.Slice(4, (int)length);
        return true;
    }

    private static bool TakeRecord(ref ReadOnlySpan<byte> data, out ReadOnlySpan<byte> record)
    {
        record = default;
        if (data.Length < 2) return false;
        int length = U16(data, 0);
        if (length > data.Length - 2) return false;
        record = data.Slice(2, length);
        data = data[(2 + length)..];
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
