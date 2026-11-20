using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

[ApiController]
[Authorize]
[Route("MaftySubtitleTitles/DiscVision/{itemId:guid}/{sourceId:guid}")]
public sealed class DiscVisionController(ILibraryManager library, IUserManager users, IApplicationPaths paths) : ControllerBase
{
    public sealed record DiscVision(string Status, int? DolbyVisionProfile, int? VerifiedPlaylist);

    [HttpGet]
    public async Task<ActionResult<DiscVision>> Get(Guid itemId, Guid sourceId, CancellationToken ct)
    {
        // Same authenticated identity and user-scoped item lookup used by Jellyfin's Items endpoint.
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty
            || users.GetUserById(userId) is not { } user || library.GetItemById<BaseItem>(itemId, user) is not { } item)
            return NotFound();
        var source = item.GetMediaSources(false).FirstOrDefault(source => Guid.TryParse(source.Id, out var id) && id == sourceId);
        if (source?.Path is not { } path || !DiscCache.IsIso(path)) return NotFound();
        var stamp = DiscCache.Stamp.Read(path);
        var result = stamp is null ? null : await new DiscCache(paths.DataPath).Read(sourceId, stamp, ct).ConfigureAwait(false);
        return new DiscVision(result?.Status ?? "not-scanned", result?.Profile, result?.VerifiedPlaylist);
    }
}
