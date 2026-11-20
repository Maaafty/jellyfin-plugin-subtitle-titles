using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal enum AssEffects { None, Limited, Full }

internal static partial class Recognition
{
    private const double MinimumBitmapOcr = 55;
    private const double MinimumBitmapLineWeight = .40;
    private const double BitmapRegionCap = 2;
    private const double MinimumBitmapSupport = 2;
    private const double MinimumBitmapOutsidePeak = .55;

    private sealed class BitmapRegion
    {
        internal double Coverage;
        internal readonly Dictionary<string, double> Languages = new(StringComparer.Ordinal);
        internal readonly Dictionary<(string, string), double> Pairs = [];
    }

    private readonly record struct BitmapSupport(double Total, double Peak)
    {
        internal BitmapSupport Add(double weight) => new(Total + weight, Math.Max(Peak, weight));
        internal double OutsidePeak => Total - Peak;
    }

    private readonly struct BitmapEvidence(Dictionary<string, (string Code, string Key, double Confidence)> identities,
        Dictionary<(string Code, string Key), int> firstImage)
    {
        internal bool TryGet(BitmapLine line, int image, out string code, out double weight)
        {
            code = "";
            weight = 0;
            if (line.Confidence < MinimumBitmapOcr || !identities.TryGetValue(line.Text, out var identity)
                || firstImage[(identity.Code, identity.Key)] != image) return false;
            // These uncalibrated scores are attenuation factors, not estimated probabilities.
            weight = line.Confidence / 100 * identity.Confidence;
            code = identity.Code;
            return weight >= MinimumBitmapLineWeight;
        }
    }

    internal static async Task<(Language Chinese, string Languages)> DetectText(IEnumerable<string> cues, BundledTools tools,
        string work, CancellationToken ct)
    {
        var rows = new List<string[]>();
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cue in cues.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var parts = cue.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Count(char.IsLetter) >= 5).ToArray();
            if (parts.Length == 0) continue;
            rows.Add(parts);
            foreach (var line in parts) lines.Add(line);
        }
        if (lines.Count < 8) return (Language.Unknown, "");
        var predictions = await PredictLanguages(lines, tools, work, ct).ConfigureAwait(false);
        var byLanguage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (line, prediction) in predictions)
        {
            if (!byLanguage.TryGetValue(prediction.Code, out var evidence))
                byLanguage[prediction.Code] = evidence = new(StringComparer.Ordinal);
            evidence.Add(line);
        }
        if (byLanguage.Count == 0) return (Language.Unknown, "");
        var primary = byLanguage.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
        if (primary.Value.Count < 8) return (Language.Unknown, "");
        var paired = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.Any(line => predictions.GetValueOrDefault(line).Code == primary.Key)) continue;
            foreach (var line in row)
            {
                if (!predictions.TryGetValue(line, out var prediction) || prediction.Code == primary.Key) continue;
                if (!paired.TryGetValue(prediction.Code, out var evidence))
                    paired[prediction.Code] = evidence = new(StringComparer.Ordinal);
                evidence.Add(line);
            }
        }
        var languages = byLanguage.Where(pair => pair.Key == primary.Key ||
            (pair.Value.Count * 5L >= primary.Value.Count * 2L && paired.TryGetValue(pair.Key, out var evidence)
                && evidence.Count * 2L >= pair.Value.Count && evidence.Count >= 8))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        var script = Language.Unknown;
        if (Array.IndexOf(languages, "zh") >= 0)
        {
            var catalog = new StringBuilder();
            foreach (var line in byLanguage["zh"]) catalog.AppendLine(line);
            script = await ChineseScript(catalog, tools, work, ct).ConfigureAwait(false);
        }
        return (script, string.Join(',', languages));
    }

    internal static async Task<(Language Chinese, string Languages)> DetectBitmap(BitmapImage[] images, BundledTools tools,
        string work, CancellationToken ct)
    {
        if (await FindBitmapEvidence(images, tools, work, chineseOnly: false, ct).ConfigureAwait(false)
            is not { } evidence) return (Language.Unknown, "");

        var regions = new Dictionary<int, BitmapRegion>();
        var strongest = new Dictionary<string, double>(StringComparer.Ordinal);
        var chinese = new StringBuilder();
        for (int i = 0; i < images.Length; i++)
        {
            var image = images[i];
            ct.ThrowIfCancellationRequested();
            double coverage = 0;
            foreach (var line in image.Lines)
            {
                if (!evidence.TryGet(line, i, out var code, out var weight)) continue;
                strongest[code] = Math.Max(weight, strongest.GetValueOrDefault(code));
                coverage = Math.Max(coverage, weight);
                if (code == "zh") chinese.AppendLine(line.Text);
            }
            if (coverage == 0) continue;
            if (!regions.TryGetValue(image.Segment, out var region)) regions[image.Segment] = region = new();
            region.Coverage += coverage;
            // Multiple wrapped lines of one language do not raise its vote. A pair is only as strong as its weaker line.
            foreach (var (code, weight) in strongest)
            {
                region.Languages[code] = region.Languages.GetValueOrDefault(code) + weight;
                foreach (var (other, otherWeight) in strongest)
                {
                    if (StringComparer.Ordinal.Compare(code, other) >= 0) continue;
                    var pair = (code, other);
                    region.Pairs[pair] = region.Pairs.GetValueOrDefault(pair) + Math.Min(weight, otherWeight);
                }
            }
            strongest.Clear();
        }

        var totals = new Dictionary<string, BitmapSupport>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string, string), BitmapSupport>();
        double totalCoverage = 0;
        foreach (var region in regions.Values)
        {
            // Cap productive region mass, scaling its votes by the same factor. A dense region cannot swamp the others.
            var mass = Math.Min(region.Coverage, BitmapRegionCap);
            var scale = mass / region.Coverage;
            totalCoverage += mass;
            foreach (var (code, weight) in region.Languages)
                totals[code] = totals.GetValueOrDefault(code).Add(scale * weight);
            foreach (var (pair, weight) in region.Pairs)
                pairs[pair] = pairs.GetValueOrDefault(pair).Add(scale * weight);
        }
        if (totals.Count == 0) return (Language.Unknown, "");
        var primary = totals.OrderByDescending(pair => pair.Value.Total).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
        if (primary.Value.Total < MinimumBitmapSupport || primary.Value.OutsidePeak < MinimumBitmapOutsidePeak
            || primary.Value.Total < .55 * totalCoverage) return (Language.Unknown, "");

        var languages = new List<string> { primary.Key };
        foreach (var (code, support) in totals)
        {
            if (code == primary.Key) continue;
            var key = StringComparer.Ordinal.Compare(primary.Key, code) < 0 ? (primary.Key, code) : (code, primary.Key);
            var paired = pairs.GetValueOrDefault(key);
            if (paired.Total >= MinimumBitmapSupport && paired.OutsidePeak >= MinimumBitmapOutsidePeak
                && paired.Total >= .30 * totalCoverage && paired.Total >= .55 * support.Total)
                languages.Add(code);
            else if (support.Total >= .75 && support.Total >= .18 * totalCoverage)
                return (Language.Unknown, ""); // A credible competing language without adequate pairing is not confident monolingual evidence.
        }
        languages.Sort(StringComparer.Ordinal);
        var script = languages.Contains("zh")
            ? await ChineseScript(chinese, tools, work, ct).ConfigureAwait(false) : Language.Unknown;
        return (script, string.Join(',', languages));
    }

    internal static async Task<Language> DetectBitmapScript(BitmapImage[] images, BundledTools tools, string work, CancellationToken ct)
    {
        if (await FindBitmapEvidence(images, tools, work, chineseOnly: true, ct).ConfigureAwait(false)
            is not { } evidence) return Language.Chinese;

        var chinese = new StringBuilder();
        var regions = new Dictionary<int, double>();
        for (int i = 0; i < images.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var image = images[i];
            double strongest = 0;
            foreach (var line in image.Lines)
            {
                if (!evidence.TryGet(line, i, out _, out var weight)) continue;
                chinese.AppendLine(line.Text);
                strongest = Math.Max(strongest, weight);
            }
            if (strongest > 0) regions[image.Segment] = regions.GetValueOrDefault(image.Segment) + strongest;
        }

        // The first pass already identified the languages. Only require independent Chinese OCR evidence here.
        var support = default(BitmapSupport);
        foreach (var weight in regions.Values) support = support.Add(Math.Min(weight, BitmapRegionCap));
        return support.Total >= MinimumBitmapSupport && support.OutsidePeak >= MinimumBitmapOutsidePeak
            ? await ChineseScript(chinese, tools, work, ct).ConfigureAwait(false) : Language.Chinese;
    }

    private static async Task<BitmapEvidence?> FindBitmapEvidence(BitmapImage[] images, BundledTools tools,
        string work, bool chineseOnly, CancellationToken ct)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var line in image.Lines)
            {
                if (line.Confidence < MinimumBitmapOcr || lines.Contains(line.Text)) continue;
                if ((!chineseOnly && line.Text.Count(char.IsLetter) >= 5) || Han().Count(line.Text) >= 3) lines.Add(line.Text);
            }
        }
        if (lines.Count == 0) return null;
        var predictions = await PredictLanguages(lines, tools, work, ct).ConfigureAwait(false);
        var identities = new Dictionary<string, (string Code, string Key, double Confidence)>(StringComparer.Ordinal);
        foreach (var (text, prediction) in predictions)
        {
            if (!double.IsFinite(prediction.Confidence)
                || (chineseOnly ? prediction.Code != "zh" : prediction.Code == "zh" && !Han().IsMatch(text))) continue;
            identities.Add(text, (prediction.Code, BitmapKey(text), Math.Min(1, prediction.Confidence)));
        }

        // A line found in two images is not independent dialogue (even if only the punctuation or spacing differs).
        // Compare individual lines within their predicted language, not the full bitmap or the paired wording.
        var firstImage = new Dictionary<(string Code, string Key), int>();
        for (int i = 0; i < images.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var line in images[i].Lines)
            {
                if (!identities.TryGetValue(line.Text, out var identity)) continue;
                var key = (identity.Code, identity.Key);
                if (!firstImage.TryAdd(key, i) && firstImage[key] != i) firstImage[key] = -1;
            }
        }
        return new BitmapEvidence(identities, firstImage);
    }

    private static string BitmapKey(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) key.Append(char.ToUpperInvariant(c));
        return key.ToString();
    }

    private static async Task<Dictionary<string, (string Code, double Confidence)>> PredictLanguages(IEnumerable<string> lines,
        BundledTools tools, string work, CancellationToken ct)
    {
        var input = Path.Combine(work, "language-input.txt");
        await File.WriteAllLinesAsync(input, lines, ct).ConfigureAwait(false);
        var output = await tools.PredictLanguages(input, ct).ConfigureAwait(false);
        var predictions = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        using var reader = new StringReader(output);
        foreach (var line in lines)
        {
            var prediction = reader.ReadLine() ?? throw new InvalidDataException("Incomplete fastText language output.");
            int space = prediction.IndexOf(' ');
            const string prefix = "__label__";
            if (space <= prefix.Length || !prediction.StartsWith(prefix, StringComparison.Ordinal)
                || !double.TryParse(prediction.AsSpan(space + 1), CultureInfo.InvariantCulture, out var confidence) || confidence < .65) continue;
            var code = prediction[prefix.Length..space];
            if (code == "ja" && !Kana().IsMatch(line)) continue; // Han-only lines do not establish Japanese.
            if (code == "ko" && !Hangul().IsMatch(line)) continue;
            if (code == "zh" && NonChinese().IsMatch(line)) continue;
            predictions.Add(line, (code, confidence));
        }
        return predictions;
    }

    private static async Task<Language> ChineseScript(StringBuilder corpus, BundledTools tools, string work, CancellationToken ct)
    {
        var input = Path.Combine(work, "corpus.txt");
        await File.WriteAllTextAsync(input, corpus.ToString(), ct).ConfigureAwait(false);
        var simplified = await tools.Convert("t2s.json", input, ct).ConfigureAwait(false);
        var traditional = await tools.Convert("s2t.json", input, ct).ConfigureAwait(false);
        // Use distinct changed words/phrases as evidence, not repetition of the same name or OCR error.
        var originalLines = corpus.ToString().Split('\n');
        int toSimplified = Differences(originalLines, simplified), toTraditional = Differences(originalLines, traditional);
        if (toTraditional >= 5 && toTraditional >= 4 * toSimplified) return Language.Simplified;
        if (toSimplified >= 5 && toSimplified >= 4 * toTraditional) return Language.Traditional;
        return Language.Chinese;
    }

    private static int Differences(string[] original, string converted)
    {
        var changes = new HashSet<char>();
        foreach (var (a, b) in original.Zip(converted.Split('\n')))
        {
            if (a.Length != b.Length) continue;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] && Han().IsMatch(a.AsSpan(i, 1))) changes.Add(a[i]);
        }
        return changes.Count;
    }

    internal static (List<string> Cues, AssEffects Effects) Ass(IEnumerable<string> lines)
    {
        bool events = false, hasEffects = false;
        var intervals = new Dictionary<TimeSpan, bool>();
        var cues = new Dictionary<(string Start, string End), StringBuilder>();
        foreach (var line in lines)
        {
            if (line.StartsWith('[')) events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            if (!events || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line[9..].Split(',', 10);
            if (fields.Length != 10) continue;
            var text = fields[9];
            var tags = Override().Matches(text);
            bool strong = tags.Any(tag => AdvancedEffect().IsMatch(tag.Value));
            hasEffects |= strong || tags.Any(tag => Fade().IsMatch(tag.Value));
            if (TimeSpan.TryParse(fields[1], CultureInfo.InvariantCulture, out var timecode))
            {
                if (strong) intervals[timecode] = true;
                else intervals.TryAdd(timecode, false);
            }
            var builder = new StringBuilder();
            bool drawing = false;
            int start = 0;
            foreach (Match tag in tags)
            {
                if (!drawing) builder.Append(text.AsSpan(start, tag.Index - start));
                foreach (Match mode in Drawing().Matches(tag.Value)) drawing = mode.Groups[1].Value.TrimStart('0').Length != 0;
                start = tag.Index + tag.Length;
            }
            if (!drawing) builder.Append(text.AsSpan(start));
            if (builder.Length == 0) continue;
            // A bilingual ASS often puts the two lines in separate Dialogue events over the same interval.
            var time = (fields[1].Trim(), fields[2].Trim());
            if (!cues.TryGetValue(time, out var cue)) cues[time] = cue = new StringBuilder();
            if (cue.Length > 0) cue.Append('\n');
            cue.Append(builder.ToString().Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " "));
        }
        return (cues.Values.Select(cue => cue.ToString()).ToList(), ClassifyEffects(hasEffects, intervals));
    }

    private static AssEffects ClassifyEffects(bool hasEffects, Dictionary<TimeSpan, bool> intervals)
    {
        if (!hasEffects) return AssEffects.None;
        if (intervals.Count < 12) return AssEffects.Limited;
        var start = intervals.Keys.Min();
        var duration = (intervals.Keys.Max() - start).TotalSeconds;
        if (duration == 0) return AssEffects.Limited;
        int advanced = 0, thirds = 0;
        foreach (var (time, strong) in intervals)
        {
            if (!strong) continue;
            advanced++;
            thirds |= 1 << Math.Min(2, (int)(3 * (time - start).TotalSeconds / duration));
        }
        return advanced >= 12 && advanced * 10L >= intervals.Count && thirds == 0b111 ? AssEffects.Full : AssEffects.Limited;
    }

    internal static IEnumerable<string> Srt(string text)
    {
        foreach (var block in BlankLine().Split(text))
        {
            var lines = block.Split('\n');
            var timestamp = Array.FindIndex(lines, line => line.Contains(" --> ", StringComparison.Ordinal));
            if (timestamp >= 0) yield return Markup().Replace(string.Join('\n', lines.Skip(timestamp + 1)), "");
        }
    }

    [GeneratedRegex(@"[\u3400-\u4dbf\u4e00-\u9fff]")] private static partial Regex Han();
    [GeneratedRegex(@"[\u3041-\u3096\u30a1-\u30fa\uff66-\uff9d]")] private static partial Regex Kana();
    [GeneratedRegex(@"[\uac00-\ud7a3\u1100-\u11ff\u3130-\u318f]")] private static partial Regex Hangul();
    [GeneratedRegex(@"[\u3041-\u3096\u30a1-\u30fa\uff66-\uff9d\uac00-\ud7a3]")] private static partial Regex NonChinese();
    [GeneratedRegex(@"\r?\n[\t ]*\r?\n")] private static partial Regex BlankLine();
    [GeneratedRegex(@"<[^>]*>|\{[^}]*\}")] private static partial Regex Markup();
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex Override();
    [GeneratedRegex(@"\\p\s*(\d+)")] private static partial Regex Drawing();
    [GeneratedRegex(@"\\(?:t|move)\s*\(|\\(?:p\s*0*[1-9]|(?:K|k[oft]?)\s*\d+)")] private static partial Regex AdvancedEffect();
    [GeneratedRegex(@"\\(?:fade|fad)\s*\(")] private static partial Regex Fade();
}
