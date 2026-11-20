using System.Diagnostics;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

internal static class Tool
{
    internal static async Task<string> Run(string program, IEnumerable<string> args, CancellationToken cancellation, TimeSpan timeout, string? libraryPath = null)
        => (await RunWithDiagnostics(program, args, cancellation, timeout, libraryPath).ConfigureAwait(false)).Output;

    internal static async Task<(string Output, string Diagnostics)> RunWithDiagnostics(string program, IEnumerable<string> args,
        CancellationToken cancellation, TimeSpan timeout, string? libraryPath = null)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["OMP_THREAD_LIMIT"] = "1";
        if (libraryPath is not null) info.Environment["LD_LIBRARY_PATH"] = libraryPath;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {program}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException($"{program} exceeded {timeout}");
        }
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException($"{program} exited {process.ExitCode}: {error.Trim()}");
        return (await stdout.ConfigureAwait(false), error);
    }
}
