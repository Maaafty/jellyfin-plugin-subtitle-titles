using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MaftySubtitleTitles;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    internal const string DisplayName = "mReel Media Inspector";
    internal static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;

    public override Guid Id => new("ee4b9a4b-740c-49f4-b4ea-72fd212fc51e");
    public override string Name => DisplayName;
    public override string Description => "The Jellyfin companion plugin for mReel: identify subtitles and Blu-ray Dolby Vision without modifying media files.";

    internal BundledTools Tools => new(Path.GetDirectoryName(AssemblyFilePath)!);

    public IEnumerable<PluginPageInfo> GetPages() =>
        [new() { Name = "mafty-subtitle-titles", EmbeddedResourcePath = $"{GetType().Namespace}.Settings.html" }];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public Guid[] LibraryIds { get; set; } = [];
    public bool ApplyTitles { get; set; }
    public bool RestoreTitles { get; set; }
    public bool EnableBitmapOcr { get; set; }
    public bool ReuseSeasonSubtitles { get; set; }
}
