using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.SubtitleDownload;

public sealed class Plugin : BasePlugin
{
    public Plugin(IApplicationPaths paths)
    {
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.Combine(paths.PluginsPath, "SubtitleDownload"), assembly.GetName().Version!);
    }

    public static readonly Guid PluginId = new("b8ec4802-7283-4cb3-b4b3-1f7d68b293ca");
    public override string Name => "Subtitle Download";
    public override string Description => "Download all detected subtitles from a movie or episode's menu.";
    public override Guid Id => PluginId;
}
