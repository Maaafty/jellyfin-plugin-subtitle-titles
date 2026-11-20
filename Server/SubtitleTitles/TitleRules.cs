using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal enum Language { Unknown, Chinese, Simplified, Traditional, ChineseEnglish, SimplifiedEnglish, TraditionalEnglish }

internal static partial class TitleRules
{
    private const string Separator = " · ";
    private readonly record struct LanguageTag(Language Script, string[]? Codes, bool ScriptConflict = false, string? Regions = null);
    private readonly record struct TitlePart(string Raw, LanguageTag Tag, string? Detail)
    {
        internal string[]? Codes => Tag.Codes;
    }
    private readonly record struct ParsedTitle(string? Original, TitlePart[] Parts, bool ReleaseHint);
    private readonly record struct LanguageEvidence(Language Script, string[] Codes);
    private readonly record struct TitleDecision(LanguageEvidence Display, bool Conflict);

    private static readonly Dictionary<string, string> StandardAliases = BuildStandardAliases();
    private static readonly Dictionary<string, Language> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["中文"] = Language.Chinese, ["Chinese"] = Language.Chinese, ["汉语"] = Language.Chinese, ["漢語"] = Language.Chinese,
        ["简体中文"] = Language.Simplified, ["簡體中文"] = Language.Simplified, ["简体"] = Language.Simplified,
        ["簡體"] = Language.Simplified, ["zh-Hans"] = Language.Simplified, ["Chinese(Simplified)"] = Language.Simplified,
        ["繁体中文"] = Language.Traditional, ["繁體中文"] = Language.Traditional, ["繁体"] = Language.Traditional,
        ["繁體"] = Language.Traditional, ["zh-Hant"] = Language.Traditional, ["Chinese(Traditional)"] = Language.Traditional,
        ["Simplified"] = Language.Simplified, ["Traditional"] = Language.Traditional,
        ["中文(简)"] = Language.Simplified, ["中文(簡)"] = Language.Simplified,
        ["中文(简体)"] = Language.Simplified, ["中文(簡體)"] = Language.Simplified,
        ["中文(繁)"] = Language.Traditional, ["中文(繁体)"] = Language.Traditional, ["中文(繁體)"] = Language.Traditional,
        ["简体单语"] = Language.Simplified, ["簡體單語"] = Language.Simplified,
        ["繁体单语"] = Language.Traditional, ["繁體單語"] = Language.Traditional,
        ["中英双语"] = Language.ChineseEnglish, ["中英雙語"] = Language.ChineseEnglish,
        ["简英双语"] = Language.SimplifiedEnglish, ["簡英雙語"] = Language.SimplifiedEnglish,
        ["繁英双语"] = Language.TraditionalEnglish, ["繁英雙語"] = Language.TraditionalEnglish,
        ["中英"] = Language.ChineseEnglish, ["简英"] = Language.SimplifiedEnglish,
        ["簡英"] = Language.SimplifiedEnglish, ["繁英"] = Language.TraditionalEnglish,
        ["zh"] = Language.Chinese, ["zho"] = Language.Chinese, ["chi"] = Language.Chinese, ["cmn"] = Language.Chinese,
        ["chs"] = Language.Simplified, ["zh-CN"] = Language.Simplified, ["zh-SG"] = Language.Simplified,
        ["cht"] = Language.Traditional, ["zh-TW"] = Language.Traditional,
        ["zh-HK"] = Language.Traditional, ["zh-MO"] = Language.Traditional,
        ["简中"] = Language.Simplified, ["簡中"] = Language.Simplified, ["繁中"] = Language.Traditional,
    };

    internal static string? Suggest(string? original, Language detected, AssEffects effects, out bool conflict,
        string languages = "", string? trackLanguage = null)
    {
        var title = ParseTitle(original);
        var evidence = FromRecognition(detected, languages);
        var decision = Resolve(title, evidence);
        conflict = decision.Conflict || HasRegionalConflict(title, evidence, trackLanguage);
        return conflict ? original : Render(title, decision.Display, evidence, effects);
    }

    private static ParsedTitle ParseTitle(string? original)
    {
        var raw = (original ?? "").Split(Separator, StringSplitOptions.None);
        var parts = new TitlePart[raw.Length];
        for (var i = 0; i < raw.Length; i++) parts[i] = SplitTag(raw[i]);
        var match = ReleaseLanguage().Match(raw[0].Trim());
        var releaseHint = parts[0].Codes is null && match.Success;
        if (releaseHint)
            parts[0] = new(raw[0], new(match.Groups[2].Value.Equals("SC", StringComparison.OrdinalIgnoreCase)
                ? Language.Simplified : Language.Traditional, ["zh"]), match.Groups[1].Value);
        return new(original, parts, releaseHint);
    }

    private static LanguageEvidence FromRecognition(Language detected, string languages)
    {
        var codes = languages.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (codes.Length == 0 && detected != Language.Unknown)
            codes = Bilingual(detected) ? ["zh", "en"] : ["zh"];
        return new(Script(detected), codes);
    }

    internal static (string? Declared, string? Detected) DescribeEvidence(string? original, Language detected, string languages)
    {
        var (declared, _) = ReadDeclaration(ParseTitle(original));
        return (LanguageLabel(declared), LanguageLabel(FromRecognition(detected, languages)));
    }

    private static (LanguageEvidence Evidence, bool ScriptConflict) ReadDeclaration(ParsedTitle title)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var script = Language.Unknown;
        var conflict = false;
        foreach (var part in title.Parts)
        {
            if (part.Codes is not { } codes) continue;
            declared.UnionWith(codes);
            conflict |= part.Tag.ScriptConflict;
            var declaredScript = part.Tag.Script;
            if (declaredScript == Language.Unknown) continue;
            if (script != Language.Unknown && script != declaredScript) conflict = true;
            script = declaredScript;
        }
        return (new(conflict ? Language.Unknown : script, declared.ToArray()), conflict);
    }

    private static TitleDecision Resolve(ParsedTitle title, LanguageEvidence observed)
    {
        var (declaration, scriptConflict) = ReadDeclaration(title);
        var declared = declaration.Codes;
        var script = declaration.Script;
        if (scriptConflict) return new(observed, true);
        if (observed.Codes.Length == 0 && !title.ReleaseHint) return new(observed, false);
        if (script != Language.Unknown && observed.Script != Language.Unknown && script != observed.Script)
            return new(observed, true);
        if (declared.Length > 0 && observed.Codes.Length > 0 && !declared.Intersect(observed.Codes, StringComparer.Ordinal).Any())
            return new(observed, true);
        // A narrower sample does not refute a language. Two incompatible multi-language sets do.
        if (declared.Length > 1 && observed.Codes.Length > 1 && !declared.All(observed.Codes.Contains)
            && !observed.Codes.All(declared.Contains)) return new(observed, true);
        var combined = declared.Length == 0 ? observed.Codes : observed.Codes.Union(declared, StringComparer.Ordinal).ToArray();
        return new(new(observed.Script == Language.Unknown ? script : observed.Script, combined), false);
    }

    private static string? LanguageLabel(LanguageEvidence display)
    {
        var codes = display.Codes;
        var script = display.Script;
        var hasChinese = codes.Contains("zh");
        var others = codes.Where(code => code != "zh").ToArray();
        var chinese = script switch { Language.Simplified => "简体中文", Language.Traditional => "繁体中文", _ => "中文" };
        return (hasChinese, others.Length) switch
        {
            (false, 0) => null,
            (true, 0) => chinese,
            (true, 1) when others[0] == "en" => script switch
                { Language.Simplified => "简英双语", Language.Traditional => "繁英双语", _ => "中英双语" },
            (true, 1) when others[0] == "ja" => script switch
                { Language.Simplified => "简日双语", Language.Traditional => "繁日双语", _ => "中日双语" },
            (true, _) => chinese + " / " + string.Join(" / ", others.Select(LanguageName)),
            _ => string.Join(" / ", others.Select(LanguageName)),
        };
    }

    private static string? Render(ParsedTitle title, LanguageEvidence display, LanguageEvidence observed, AssEffects effects)
    {
        var label = LanguageLabel(display);

        var parts = title.Parts;
        var main = parts[0].Raw.Trim();
        bool addEffect = effects == AssEffects.Full && !parts.Any(part => part.Raw.Trim() is "特效" or "特效字幕" or "完整特效"
            || part.Detail?.Trim() is "特效" or "特效字幕" or "完整特效");
        if (label is null)
        {
            if (!addEffect) return title.Original;
            if (main.Length == 0) return string.Join(Separator, parts.Skip(1).Select(part => part.Raw).Prepend("特效"));
            var original = title.Original!;
            var boundary = original.IndexOf(Separator, StringComparison.Ordinal);
            return boundary < 0 ? original + Separator + "特效" : original.Insert(boundary, Separator + "特效");
        }

        var result = new List<string>(parts.Length + 2) { label };
        if (addEffect) result.Add("特效");
        var skipMain = Placeholder().IsMatch(main)
            || (parts.Length == 1 && observed.Codes is [var code] && IsRedundantCountry(main, code));
        for (var i = skipMain ? 1 : 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Codes is not null)
            {
                if (part.Detail is { Length: > 0 } detail) result.Add(detail);
            }
            else if (!part.Raw.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) result.Add(part.Raw);
        }
        return string.Join(Separator, result);
    }

    private static readonly Dictionary<string, RegionInfo> SingleRegions = BuildSingleRegions();
    private static readonly Dictionary<string, HashSet<string>> RegionalLanguages = BuildRegionalLanguages();

    private static Dictionary<string, RegionInfo> BuildSingleRegions()
    {
        var regions = new Dictionary<string, RegionInfo>(StringComparer.Ordinal);
        var multiple = new HashSet<string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var code = culture.TwoLetterISOLanguageName;
            if (multiple.Contains(code)) continue;
            var region = new RegionInfo(culture.Name);
            if (region.TwoLetterISORegionName.Length != 2) continue;
            if (regions.TryAdd(code, region) || regions[code].TwoLetterISORegionName == region.TwoLetterISORegionName) continue;
            regions.Remove(code);
            multiple.Add(code);
        }
        return regions;
    }

    private static Dictionary<string, HashSet<string>> BuildRegionalLanguages()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var region = new RegionInfo(culture.Name);
            if (region.TwoLetterISORegionName.Length != 2) continue;
            Add(region.EnglishName, culture.TwoLetterISOLanguageName);
            Add(region.NativeName, culture.TwoLetterISOLanguageName);
        }
        return result;

        void Add(string country, string code)
        {
            if (!result.TryGetValue(country, out var codes)) result.Add(country, codes = new(StringComparer.Ordinal));
            codes.Add(code);
        }
    }

    // A country alone is not a language. Only use it to corroborate an existing stream language tag.
    private static bool HasRegionalConflict(ParsedTitle title, LanguageEvidence observed, string? trackLanguage)
    {
        if (trackLanguage is null || observed.Codes is not [var detected]
            || title.Parts is not [{ Codes: null, Raw: var raw }]
            || !RegionalLanguages.TryGetValue(raw.Trim(), out var regional)) return false;
        var tagged = ReadTag(trackLanguage).Codes;
        if (tagged is not [var code] || code == detected || !regional.Contains(code)) return false;
        return !regional.Contains(detected)
            && !regional.Contains(CultureInfo.CreateSpecificCulture(detected).TwoLetterISOLanguageName);
    }

    // This is a display cleanup, not recognition evidence. Explicit locale or version details stay intact.
    private static bool IsRedundantCountry(string title, string code) =>
        SingleRegions.TryGetValue(code, out var region)
        && (title.Equals(region.EnglishName, StringComparison.OrdinalIgnoreCase)
            || title.Equals(region.NativeName, StringComparison.OrdinalIgnoreCase));

    private static string LanguageName(string code) => code switch
    {
        "en" => "英语", "ja" => "日语", "ko" => "韩语", "fr" => "法语", "de" => "德语",
        "es" => "西班牙语", "ru" => "俄语", "it" => "意大利语", "pt" => "葡萄牙语",
        "ar" => "阿拉伯语", "th" => "泰语", "vi" => "越南语", "yue" => "粤语", "cs" => "捷克语",
        _ => CultureInfo.GetCultureInfo(code).EnglishName
    };

    private static TitlePart SplitTag(string raw)
    {
        var title = raw.Trim();
        var tag = ReadTag(title);
        if (tag.Codes is not null) return Part(tag);
        // Strip only a recognized label at a boundary; keep the remaining text as supplied.
        for (int i = title.Length - 1; i > 0; i--)
        {
            if (title[i] is '[' or '【' or ':' or '：' or '-' or '–' or '—')
            {
                tag = ReadTag(title[..i].TrimEnd());
                if (tag.Codes is not null) return Part(tag, title[i..].TrimStart(' ', '-', '–', '—', ':', '：'));
            }
            else if (char.IsWhiteSpace(title[i]) || title[i] is '(' or '（')
            {
                var qualifier = title[i..].Trim().Trim('(', ')', '（', '）');
                if (!TrackQualifier().IsMatch(qualifier)) continue;
                tag = ReadTag(title[..i].TrimEnd());
                if (tag.Codes is not null) return Part(tag, qualifier);
            }
        }
        if (title.Length > 2 && title[^1] is ']' or '】')
        {
            var start = title.LastIndexOf(title[^1] == ']' ? '[' : '【');
            if (start > 0)
            {
                tag = ReadTag(title[(start + 1)..^1].Trim());
                if (tag.Codes is not null) return Part(tag, title[..start].TrimEnd());
            }
        }
        return new(raw, default, null);

        TitlePart Part(LanguageTag language, string? detail = null) => new(raw, language,
            language.Regions is null ? detail : string.IsNullOrEmpty(detail) ? language.Regions : language.Regions + Separator + detail);
    }

    private static LanguageTag ReadTag(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && ((text[0] == '[' && text[^1] == ']') || (text[0] == '【' && text[^1] == '】')))
            text = AdjacentTags().Replace(text[1..^1].Trim(), "/");
        var parsed = ReadLanguageList(text);
        if (parsed.Codes is not null) return parsed;
        var separated = CrossLanguageSeparator().Replace(text, "/");
        return ReferenceEquals(text, separated) ? default : ReadLanguageList(separated);
    }

    private static LanguageTag ReadLanguageList(string text)
    {
        var parts = text.Split(['/', '／', '+', '＋', '&', '＆', ',', '，', '、', '_'], StringSplitOptions.TrimEntries);
        var script = Language.Unknown;
        bool scriptConflict = false;
        string? regions = null;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            // A generic subtitle suffix is part of the language label only if the base token resolves.
            var token = SubtitleSuffix().Replace(parts[i], "").TrimEnd();
            bool transportLabel = i == 0 && token.StartsWith("TX-", StringComparison.OrdinalIgnoreCase);
            if (transportLabel) token = token[3..];
            var chinese = Known.GetValueOrDefault(token.Replace('（', '(').Replace('）', ')')
                .Replace(" (", "(", StringComparison.Ordinal));
            if (chinese != Language.Unknown)
            {
                RecordScript(chinese);
                if (token.Length == 5 && token.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
                    regions = regions is null ? token : regions + Separator + token;
                codes.Add("zh");
                if (Bilingual(chinese)) codes.Add("en");
                continue;
            }
            if (transportLabel) return default;
            var japanese = token switch
            {
                "简日" or "簡日" or "简日双语" or "簡日雙語" => Language.Simplified,
                "繁日" or "繁日双语" or "繁日雙語" => Language.Traditional,
                "中日" or "中日双语" or "中日雙語" => Language.Chinese,
                _ => Language.Unknown,
            };
            if (japanese != Language.Unknown)
            {
                RecordScript(japanese);
                codes.Add("zh");
                codes.Add("ja");
                continue;
            }
            // Subtitle qualifiers take priority over identical ISO codes (e.g. SDH).
            if (TrackQualifier().IsMatch(token)) return default;
            var code = token.ToLowerInvariant() switch
            {
                "en" or "eng" or "english" or "英文" or "英语" or "英語" => "en",
                "ja" or "jpn" or "japanese" or "日文" or "日语" or "日語" => "ja",
                "ko" or "kor" or "korean" or "韩文" or "韓文" or "韓語" => "ko",
                "fr" or "fra" or "fre" or "french" or "法文" or "法語" => "fr",
                "de" or "deu" or "ger" or "german" or "德文" or "德語" => "de",
                "es" or "spa" or "spanish" or "西班牙文" or "西班牙語" => "es",
                "pt" or "por" or "portuguese" or "葡萄牙文" or "葡萄牙語" => "pt",
                "it" or "ita" or "italian" or "義大利語" => "it",
                "ru" or "rus" or "russian" or "俄文" or "俄語" => "ru",
                _ => StandardAliases.GetValueOrDefault(token),
            };
            if (code is null) return default;
            codes.Add(code);
        }
        return new(scriptConflict ? Language.Unknown : script, codes.ToArray(), scriptConflict, regions);

        void RecordScript(Language language)
        {
            var tokenScript = Script(language);
            if (tokenScript == Language.Unknown) return;
            if (script != Language.Unknown && script != tokenScript) scriptConflict = true;
            script = tokenScript;
        }
    }

    private static Dictionary<string, string> BuildStandardAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string code)
        {
            // Short ASCII strings can be release-group initials; keep short native names such as 粵語.
            if (name.Length == 0 || (name.Length <= 2 && name.All(char.IsAscii)) || ambiguous.Contains(name)) return;
            if (aliases.TryAdd(name, code) || aliases[name] == code) return;
            aliases.Remove(name);
            ambiguous.Add(name);
        }
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var code = culture.Name;
            if (code.Length is not (2 or 3) || code != culture.TwoLetterISOLanguageName) continue;
            Add(culture.ThreeLetterISOLanguageName, code);
            Add(culture.EnglishName, code);
            Add(culture.NativeName, code);
            Add(LanguageName(code), code);
        }
        return aliases;
    }

    [GeneratedRegex(@"[\]】]\s*[\[【]")]
    private static partial Regex AdjacentTags();

    [GeneratedRegex(@"×|\s+[xX]\s+|(?<=[)）\p{IsCJKUnifiedIdeographs}])[xX]|[xX](?=\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex CrossLanguageSeparator();

    [GeneratedRegex(@"(?:字幕|\s+subtitles?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubtitleSuffix();

    [GeneratedRegex(@"^(?:SDH|CC|HI|Forced|Commentary|Signs(?:\s*(?:&|and)\s*Songs)?|karaoke|简体注释|强制|強制|听障|聽障|评论音轨|評論音軌)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackQualifier();

    private static Language Script(Language language) => language switch
    {
        Language.Simplified or Language.SimplifiedEnglish => Language.Simplified,
        Language.Traditional or Language.TraditionalEnglish => Language.Traditional,
        _ => Language.Unknown
    };

    private static bool Bilingual(Language language)
        => language is Language.ChineseEnglish or Language.SimplifiedEnglish or Language.TraditionalEnglish;

    [GeneratedRegex(@"^(?:|und|unknown|undefined|undetermined|未知|未定义|未定義|未指定|无标题|無標題|Track\s+\d+|轨道\s*\d+|軌道\s*\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_.-]*)-(SC|TC)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseLanguage();
}
