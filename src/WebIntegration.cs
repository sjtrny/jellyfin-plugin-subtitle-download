using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SubtitleDownload;

/// <summary>Augments the served web shell without changing Jellyfin's files.</summary>
public sealed class WebIntegration : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InjectAsync);
        next(app);
    };

    private static async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value ?? "";
        var webIndex = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        var suffix = webIndex < 0 ? "" : path[webIndex..];
        if (!HttpMethods.IsGet(context.Request.Method)
            || suffix is not ("/web" or "/web/" or "/web/index.html"))
        {
            await next();
            return;
        }

        // A full, uncompressed shell is needed, including immediately after install.
        string[] headers = ["Accept-Encoding", "If-None-Match", "If-Modified-Since", "Range", "If-Range"];
        var savedHeaders = headers.ToDictionary(h => h, h => context.Request.Headers[h]);
        foreach (var header in headers) context.Request.Headers.Remove(header);

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next();
        }
        finally
        {
            context.Response.Body = original;
            foreach (var (key, value) in savedHeaders)
                if (value.Count > 0) context.Request.Headers[key] = value;
        }

        buffer.Position = 0;
        if (context.Response.StatusCode != 200
            || context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true
            || context.Response.Headers.ContainsKey("Content-Encoding"))
        {
            await buffer.CopyToAsync(original, context.RequestAborted);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0 && !html.Contains("id=\"subtitle-download-plugin\"", StringComparison.Ordinal))
        {
            var prefix = context.Request.PathBase.Value + path[..webIndex];
            var src = System.Net.WebUtility.HtmlEncode(prefix + "/SubtitleDownload/client.js?v=1.0.0");
            html = html.Insert(end, $"<script id=\"subtitle-download-plugin\" defer src=\"{src}\"></script>");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.CacheControl = "no-cache";
        await original.WriteAsync(bytes, context.RequestAborted);
    }
}
