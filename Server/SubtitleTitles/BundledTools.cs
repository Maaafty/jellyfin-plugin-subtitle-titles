using System.Globalization;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal readonly record struct BitmapLine(double Confidence, string Text);
internal readonly record struct BitmapImage(int Segment, BitmapLine[] Lines);

internal sealed class BundledTools(string pluginDirectory)
{
    private const string FastBitmapModels = "tessdata";
    private const string BestBitmapModels = "tessdata-best";
    private readonly string _root = Path.Combine(pluginDirectory, "runtime", "linux-x64");

    internal bool Available => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && File.Exists(Path.Combine(_root, "bin", "opencc"))
        && File.Exists(Path.Combine(_root, "opencc", "s2t.json"))
        && File.Exists(Path.Combine(_root, "opencc", "t2s.json"))
        && File.Exists(Path.Combine(_root, "bin", "fasttext"))
        && File.Exists(Path.Combine(_root, "lid.176.ftz"));

    internal bool BitmapAvailable => Available && File.Exists(Path.Combine(_root, "bin", "bitmap-ocr"))
        && HasBitmapModels(FastBitmapModels);

    internal bool BitmapBestModelsAvailable => HasBitmapModels(BestBitmapModels);

    internal Task<string> Convert(string config, string input, CancellationToken ct) =>
        Run("opencc", ["-c", Path.Combine(_root, "opencc", config), "-i", input], ct);

    internal Task<string> PredictLanguages(string input, CancellationToken ct) =>
        Run("fasttext", ["predict-prob", Path.Combine(_root, "lid.176.ftz"), input, "1"], ct);

    internal Task<BitmapImage[]> ReadBitmap(string path, int index, double seconds, string? sampleFile, CancellationToken ct)
    {
        var track = index.ToString(CultureInfo.InvariantCulture);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var models = Path.Combine(_root, FastBitmapModels);
        return RunBitmap(sampleFile is null ? [path, track, duration, models] : [path, track, duration, models, sampleFile], ct);
    }

    internal Task<BitmapImage[]> ReadDiscBitmap(string path, int playlist, int pid, double seconds, string? sampleFile, CancellationToken ct)
    {
        var title = playlist.ToString(CultureInfo.InvariantCulture);
        var track = pid.ToString(CultureInfo.InvariantCulture);
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var models = Path.Combine(_root, FastBitmapModels);
        return RunBitmap(sampleFile is null ? ["--bluray", path, title, track, duration, models]
            : ["--bluray", path, title, track, duration, models, sampleFile], ct);
    }

    internal Task<BitmapImage[]> ReviewBitmap(string sampleFile, CancellationToken ct) =>
        RunBitmap(["--review", sampleFile, Path.Combine(_root, BestBitmapModels)], ct);

    internal static BitmapImage[] ParseBitmap(string output)
    {
        var remaining = output.AsSpan();
        var images = new List<BitmapImage>();
        var lines = new List<BitmapLine>();
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf('\f');
            var block = (end < 0 ? remaining : remaining[..end]).Trim();
            remaining = end < 0 ? default : remaining[(end + 1)..];
            if (block.IsEmpty) continue;
            int newline = block.IndexOf('\n');
            if (newline < 0 || !int.TryParse(block[..newline].TrimEnd('\r'), NumberStyles.None,
                CultureInfo.InvariantCulture, out var segment))
                throw new InvalidDataException("Invalid bitmap OCR segment header.");
            block = block[(newline + 1)..];
            while (!block.IsEmpty)
            {
                newline = block.IndexOf('\n');
                var line = (newline < 0 ? block : block[..newline]).TrimEnd('\r');
                block = newline < 0 ? default : block[(newline + 1)..];
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1
                    || !double.TryParse(line[..tab], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                    || !(confidence is >= 0 and <= 100))
                    throw new InvalidDataException("Invalid bitmap OCR text line.");
                lines.Add(new BitmapLine(confidence, line[(tab + 1)..].ToString()));
            }
            images.Add(new BitmapImage(segment, lines.ToArray()));
            lines.Clear();
        }
        return images.ToArray();
    }

    private async Task<BitmapImage[]> RunBitmap(string[] args, CancellationToken ct)
    {
        var output = await Tool.Run(Executable("bitmap-ocr"), args, ct, TimeSpan.FromSeconds(90),
            Path.Combine(_root, "lib")).ConfigureAwait(false);
        return ParseBitmap(output);
    }

    private bool HasBitmapModels(string modelDirectory)
    {
        var directory = Path.Combine(_root, modelDirectory);
        return File.Exists(Path.Combine(directory, "chi_sim.traineddata"))
            && File.Exists(Path.Combine(directory, "chi_tra.traineddata"))
            && File.Exists(Path.Combine(directory, "eng.traineddata"));
    }

    private Task<string> Run(string program, string[] args, CancellationToken ct)
        => Tool.Run(Executable(program), args, ct, TimeSpan.FromSeconds(30), Path.Combine(_root, "lib"));

    private string Executable(string program)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Subtitle tools require a Unix runtime.");
        var binary = Path.Combine(_root, "bin", program);
        // Jellyfin's ZIP extractor does not preserve the Unix executable bit.
        var mode = File.GetUnixFileMode(binary);
        if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(binary, mode | UnixFileMode.UserExecute);
        return binary;
    }
}
