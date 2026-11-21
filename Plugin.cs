using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Common.Configuration;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;

namespace EmbyVideoOnDemand
{
    public sealed class Plugin : BasePluginSimpleUI<Options>, IHasThumbImage
    {
        private readonly IApplicationHost _host;
        private int _updatingDates;
        private readonly SemaphoreSlim _epgUpdates = new SemaphoreSlim(1);

        internal static Plugin Instance { get; private set; }

        public Plugin(IApplicationHost applicationHost) : base(applicationHost)
        {
            _host = applicationHost;
            Instance = this;
        }

        public override Guid Id => new Guid("d3ce8341-735c-44a9-8d98-76c020412a1d");
        public override string Name => "Video On Demand";
        public override string Description => "Browse a movie and TV catalog and play matches from an M3U playlist.";
        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;
        public Stream GetThumbImage() => OpenBanner();

        internal static Stream OpenBanner() =>
            typeof(Plugin).Assembly.GetManifestResourceStream("VodArtwork.Banner.jpg");


        internal Options CurrentOptions() => GetOptions();
        internal string CatalogStampPath => Path.Combine(
            _host.Resolve<IApplicationPaths>().PluginConfigurationsPath, "EmbyVideoOnDemand.catalog.sha256");

        internal VideoOnDemandChannel Channel => _host.Resolve<IChannelManager>().GetChannel<VideoOnDemandChannel>();

        protected override bool OnOptionsSaving(Options options)
        {
            options.EpgUrl = options.EpgUrl?.Trim();
            options.RequestEpgUpdate = options.RefreshEpg ||
                !string.Equals(CurrentOptions().EpgUrl?.Trim(), options.EpgUrl, StringComparison.Ordinal);
            options.RefreshEpg = false;
            options.PreviousPlaylistUrl = CurrentOptions().VodPlaylistUrl?.Trim();
            options.RequestRefresh = options.RefreshPlaylist ||
                !string.Equals(options.PreviousPlaylistUrl, options.VodPlaylistUrl?.Trim(), StringComparison.Ordinal);
            options.RefreshPlaylist = false;
            options.RequestDateUpdate = options.UpdateTvDates;
            options.UpdateTvDates = false;
            return base.OnOptionsSaving(options);
        }

        protected override void OnOptionsSaved(Options options)
        {
            base.OnOptionsSaved(options);
            if (options.RequestEpgUpdate && !string.IsNullOrWhiteSpace(options.EpgUrl))
                _ = Task.Run(() => UpdateEpg(options.EpgUrl));
            if (options.RequestRefresh)
            {
                var manager = _host.Resolve<IChannelManager>();
                manager.GetChannel<VideoOnDemandChannel>()?.QueueCatalogRefresh(manager, CatalogStampPath, options.PreviousPlaylistUrl);
            }
            if (options.RequestDateUpdate && Interlocked.Exchange(ref _updatingDates, 1) == 0)
            {
                _ = Task.Run(UpdateTvDates);
            }
        }

        private async Task UpdateEpg(string source)
        {
            await _epgUpdates.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentOptions().EpgUrl != source) return;
                var epg = await EpgIndex.Get(source, CancellationToken.None, force: true).ConfigureAwait(false);
                if (epg == null || CurrentOptions().EpgUrl != source) return;
                var library = _host.Resolve<ILibraryManager>();
                var channel = library.GetItemList(new InternalItemsQuery { Name = Name, Recursive = true })
                    .OfType<Channel>().FirstOrDefault();
                if (channel == null) return;
                var shelves = library.GetItemList(new InternalItemsQuery { ParentIds = new[] { channel.InternalId } })
                    .OfType<Folder>().Where(folder => folder.Name == "Movies" || folder.Name == "TV Shows");
                foreach (var shelf in shelves)
                {
                    foreach (var item in library.GetItemList(new InternalItemsQuery { ParentIds = new[] { shelf.InternalId } }))
                    {
                        if (CurrentOptions().EpgUrl != source) return;
                        if (!item.ProviderIds.ContainsKey(VideoOnDemandChannel.ProviderMarker)) continue;
                        var changes = CatalogMetadataProvider.ApplyEpg(item, epg);
                        if (changes != ItemUpdateType.None) item.UpdateToRepository(changes);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Video On Demand EPG metadata update failed (" + ex.GetType().Name + ").");
            }
            finally { _epgUpdates.Release(); }
        }

        private async Task UpdateTvDates()
        {
            try
            {
                var library = _host.Resolve<ILibraryManager>();
                var provider = _host.Resolve<IProviderManager>();
                var files = _host.Resolve<IFileSystem>();
                var channel = library.GetItemList(new InternalItemsQuery { Name = "Video On Demand", Recursive = true })
                    .OfType<Channel>().FirstOrDefault();
                if (channel == null) return;

                var tv = library.GetItemList(new InternalItemsQuery { ParentIds = new[] { channel.InternalId }, Name = "TV Shows" })
                    .OfType<Folder>().FirstOrDefault();
                if (tv == null) return;

                var shows = library.GetItemList(new InternalItemsQuery { ParentIds = new[] { tv.InternalId } })
                    .OfType<Folder>().Where(show => !show.PremiereDate.HasValue && !show.IsLocked &&
                        show.ProviderIds.TryGetValue(VideoOnDemandChannel.ProviderMarker, out var key) &&
                        key.StartsWith("browse:show:", StringComparison.Ordinal));
                foreach (var show in shows)
                {
                    try
                    {
                        await provider.RefreshFullItem(show, new MetadataRefreshOptions(files)
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Video On Demand show metadata refresh failed (" + ex.GetType().Name + ").");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Video On Demand TV date update failed (" + ex.GetType().Name + ").");
            }
            finally
            {
                Volatile.Write(ref _updatingDates, 0);
            }
        }

        protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
        {
            pageInfo.DisplayName = Name;
            pageInfo.EnableInMainMenu = true;
            pageInfo.MenuSection = "server";
            pageInfo.MenuIcon = "movie";
        }
    }

    public sealed class Options : EditableOptionsBase
    {
        public override string EditorTitle => "Video On Demand";
        public override string EditorDescription => "Enter the M3U playlist to browse and search in Emby.";

        [DisplayName("Video On Demand M3U URL")]
        [Description("Changing the URL refreshes Movies and TV Shows.")]
        [IsPassword]
        public string VodPlaylistUrl { get; set; } = "";

        [DisplayName("VoD EPG URL")]
        [Description("Optional XMLTV feed for movie and show metadata. Matched EPG fields take priority. Saving a new URL applies the feed to the existing VoD catalog.")]
        [IsPassword]
        public string EpgUrl { get; set; } = "";

        [DisplayName("Refresh EPG metadata")]
        [Description("Turn on and save to download the EPG again and update matching VoD titles without rescanning the playlist.")]
        public bool RefreshEpg { get; set; }

        internal bool RequestEpgUpdate { get; set; }

        [DisplayName("Check playlist for changes")]
        [Description("Turn on before saving to refresh the catalog. Updating metadata keys alone does not start a channel scan.")]
        public bool RefreshPlaylist { get; set; }

        internal bool RequestRefresh { get; set; }
        internal string PreviousPlaylistUrl { get; set; }

        [DisplayName("Fill missing TV release dates")]
        [Description("On Save, refresh metadata for existing shows without a release date. Does not scan the M3U.")]
        public bool UpdateTvDates { get; set; }

        [DisplayName("Open Watch in Emby in a new tab")]
        [Description("Applies to movie and episode playback links in the VoD webview. Off opens Emby in the same tab.")]
        public bool OpenWatchInNewTab { get; set; }


        internal bool RequestDateUpdate { get; set; }

        [DisplayName("TMDB API key or read access token")]
        [Description("Optional. Video On Demand uses TMDB for synopsis, artwork, and community ratings.")]
        [IsPassword]
        public string TmdbToken { get; set; } = "";

        [DisplayName("TheTVDB API key")]
        [Description("Optional. TheTVDB supplies synopsis, artwork, and content ratings when available.")]
        [IsPassword]
        public string TvdbKey { get; set; } = "";

        [DisplayName("TheTVDB subscriber PIN")]
        [Description("Only for subscriber-based TheTVDB keys.")]
        [IsPassword]
        public string TvdbPin { get; set; } = "";

        [DisplayName("OMDb API key")]
        [Description("Optional. OMDb supplies IMDb-linked synopsis, artwork, and ratings.")]
        [IsPassword]
        public string OmdbKey { get; set; } = "";
    }
}
