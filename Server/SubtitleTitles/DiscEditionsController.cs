using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

[ApiController]
[Authorize]
[Route("MaftySubtitleTitles/DiscEditions/{itemId:guid}/{sourceId:guid}")]
public sealed class DiscEditionsController(ILibraryManager library, IUserManager users, IApplicationPaths paths) : ControllerBase
{
    public sealed record DiscFile(string Path, long Size);
    public sealed record DiscFiles(DiscFile[] Files);

    [HttpGet]
    public async Task<ActionResult<DiscEditionCatalog>> Get(Guid itemId, Guid sourceId, CancellationToken ct)
    {
        if (DiscPath(itemId, sourceId) is not { } path) return NotFound();
        return await new DiscEditionStore(paths.DataPath).Describe(path, sourceId, ct).ConfigureAwait(false);
    }

    [HttpGet("{playlist}/Files")]
    public ActionResult<DiscFiles> Files(Guid itemId, Guid sourceId, string playlist, CancellationToken ct)
    {
        if (!DiscEditionStore.TryPlaylist(playlist, out _) || DiscPath(itemId, sourceId) is not { } path) return NotFound();
        using var volume = DiscVolume.OpenVolume(path);
        if (volume is null || DiscNavigator.ReadPlaylist(volume, playlist, ct) is not { } selected) return NotFound();
        var files = new List<DiscFile>();
        var playlistPath = PlaylistPath(playlist);
        if (!AddFile(playlistPath)) return NotFound();
        foreach (var clip in selected.Clips.Distinct(StringComparer.Ordinal))
        {
            if (!AddFile(StreamPath(clip))) return NotFound();
            AddFile(IndexPath(clip));
        }
        return new DiscFiles(files.ToArray());

        bool AddFile(string relative)
        {
            using var file = volume.Open(relative);
            if (file is null || file.Length <= 0) return false;
            files.Add(new(relative, file.Length));
            return true;
        }
    }

    [HttpGet("{playlist}/File")]
    [HttpHead("{playlist}/File")]
    public IActionResult ReadFile(Guid itemId, Guid sourceId, string playlist, [FromQuery] string path, CancellationToken ct)
    {
        if (!DiscEditionStore.TryPlaylist(playlist, out _) || DiscPath(itemId, sourceId) is not { } discPath) return NotFound();
        var volume = DiscVolume.OpenVolume(discPath);
        if (volume is null) return NotFound();
        try
        {
            if (DiscNavigator.ReadPlaylist(volume, playlist, ct) is not { } selected
                || (path != PlaylistPath(playlist) && !selected.Clips.Any(clip => path == StreamPath(clip) || path == IndexPath(clip))))
                return NotFound();
            var stream = volume.Open(path);
            if (stream is null) return NotFound();
            // Keep the ISO filesystem handle alive until ASP.NET has sent and disposed its file.
            HttpContext.Response.RegisterForDispose(volume);
            volume = null;
            return File(stream, "application/octet-stream", enableRangeProcessing: true);
        }
        finally { volume?.Dispose(); }
    }

    private string? DiscPath(Guid itemId, Guid sourceId)
    {
        // Same authenticated identity and user-scoped item lookup used by Jellyfin's Items endpoint.
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty
            || users.GetUserById(userId) is not { } user || !user.HasPermission(PermissionKind.EnableMediaPlayback)
            || library.GetItemById<Video>(itemId, user) is not { } item)
            return null;
        var source = item.GetMediaSources(false).FirstOrDefault(source => Guid.TryParse(source.Id, out var id) && id == sourceId);
        if (source is null || (sourceId != itemId && library.GetItemById<Video>(sourceId, user) is null)) return null;
        return DiscVolume.IsDisc(source.Path) ? source.Path : null;
    }

    private static string PlaylistPath(string playlist) => "BDMV/PLAYLIST/" + playlist;
    private static string StreamPath(string clip) => "BDMV/STREAM/" + clip + ".m2ts";
    private static string IndexPath(string clip) => "BDMV/CLIPINF/" + clip + ".clpi";
}
