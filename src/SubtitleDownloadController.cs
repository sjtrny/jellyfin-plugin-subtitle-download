using System.Reflection;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleDownload;

[ApiController]
[Route("SubtitleDownload")]
public sealed class SubtitleDownloadController(ILibraryManager library, IUserManager users, IMediaSourceManager sources, SubtitleArchive archives)
    : ControllerBase
{
    [HttpGet("client.js")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public ActionResult ClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.SubtitleDownload.Web.subtitle-download.js")!;
        return File(stream, "text/javascript; charset=utf-8");
    }

    [HttpGet("Items/{itemId:guid}/Info")]
    [Authorize(Policy = Policies.Download)]
    public ActionResult Info(Guid itemId)
    {
        var item = AccessibleVideo(itemId);
        if (item is null) return NotFound();
        var media = sources.GetStaticMediaSources(item, false, CurrentUser());
        return Ok(new { Count = media.Sum(s => s.MediaStreams.Count(t => t.Type == MediaStreamType.Subtitle)), FileName = SubtitleFiles.SafeName(item.Name) + ".subtitles.zip" });
    }

    [HttpGet("Items/{itemId:guid}/Download")]
    [Authorize(Policy = Policies.Download)]
    public async Task<ActionResult> Download(Guid itemId, CancellationToken cancellationToken)
    {
        var item = AccessibleVideo(itemId);
        if (item is null) return NotFound();
        var media = sources.GetStaticMediaSources(item, false, CurrentUser());
        if (!media.Any(s => s.MediaStreams.Any(t => t.Type == MediaStreamType.Subtitle)))
            return Problem(statusCode: 404, title: "No subtitles were detected for this video.");

        try
        {
            var archive = await archives.CreateAsync(media, cancellationToken);
            Response.RegisterForDispose(archive);
            Response.Headers["X-Subtitle-Count"] = archive.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Response.Headers["X-Subtitle-Failed"] = archive.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Response.Headers.CacheControl = "no-store";
            return File(archive.Stream, "application/zip", SubtitleFiles.SafeName(item.Name) + ".subtitles.zip");
        }
        catch (IOException ex)
        {
            return Problem(statusCode: 422, title: "Subtitles could not be downloaded.", detail: ex.Message);
        }
    }

    private Jellyfin.Database.Implementations.Entities.User? CurrentUser()
    {
        var id = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(id, out var userId) && userId != Guid.Empty ? users.GetUserById(userId) : null;
    }

    private Video? AccessibleVideo(Guid id)
    {
        var user = CurrentUser();
        // Only actual server API keys may have no associated user.
        if (user is null && !string.Equals(User.FindFirst("Jellyfin-IsApiKey")?.Value, "true", StringComparison.OrdinalIgnoreCase)) return null;
        var item = library.GetItemById<Video>(id, user);
        return item is not null && (user is null ? item.CanDownload() : item.CanDownload(user)) ? item : null;
    }
}
