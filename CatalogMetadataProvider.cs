using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace EmbyVideoOnDemand
{
    public sealed class CatalogMetadataProvider : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Series>, ICustomMetadataProvider<Folder>
    {
        private static readonly MetadataClient Client = new MetadataClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        private static readonly SemaphoreSlim Lookups = new SemaphoreSlim(2);
        public string Name => "Video On Demand metadata (EPG, TMDB, TheTVDB, OMDb)";

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> result, MetadataRefreshOptions options,
            LibraryOptions library, CancellationToken token) => Fetch(result.Item, false, token);

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Series> result, MetadataRefreshOptions options,
            LibraryOptions library, CancellationToken token) => Fetch(result.Item, true, token);

        public async Task<ItemUpdateType> FetchAsync(MetadataResult<Folder> result, MetadataRefreshOptions options,
            LibraryOptions library, CancellationToken token)
        {
            var folder = result.Item;
            if (folder == null || folder.IsLocked ||
                !folder.ProviderIds.TryGetValue(VideoOnDemandChannel.ProviderMarker, out var id) ||
                !id.StartsWith("browse:show:", StringComparison.Ordinal))
            {
                return ItemUpdateType.None;
            }
            var seasonSeparator = id.IndexOf(':', "browse:show:".Length);
            if (seasonSeparator < 0)
            {
                return await Fetch(folder, true, token).ConfigureAwait(false);
            }
            var settings = Plugin.Instance?.CurrentOptions();
            var show = folder.Parent;
            if (show == null || string.IsNullOrWhiteSpace(settings?.TmdbToken) ||
                !int.TryParse(id.Substring(seasonSeparator + 1), out var season))
            {
                return ItemUpdateType.None;
            }
            var usePoster = !folder.HasImage(ImageType.Primary) ||
                string.Equals(folder.PrimaryImagePath, show.PrimaryImagePath, StringComparison.Ordinal);
            var useOverview = string.IsNullOrWhiteSpace(folder.Overview);
            if (!usePoster && !useOverview)
            {
                return ItemUpdateType.None;
            }
            await Lookups.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var details = await Client.GetSeason(settings.TmdbToken, GetId(show, "Tmdb"), season, token)
                    .ConfigureAwait(false);
                var changes = ItemUpdateType.None;
                if (usePoster && SetImage(folder, ImageType.Primary, details.Poster))
                {
                    changes |= ItemUpdateType.ImageUpdate;
                }
                if (useOverview && details.Overview != null)
                {
                    folder.Overview = details.Overview;
                    changes |= ItemUpdateType.MetadataDownload;
                }
                return changes;
            }
            finally { Lookups.Release(); }
        }

        private async Task<ItemUpdateType> Fetch(BaseItem item, bool series,
            CancellationToken token)
        {
            var options = Plugin.Instance?.CurrentOptions();
            if (item == null || item.IsLocked || !IsVod(item) || options == null)
            {
                return ItemUpdateType.None;
            }

            var epg = await EpgIndex.Get(options.EpgUrl, token).ConfigureAwait(false);
            var result = ApplyEpg(item, epg);
            if (string.IsNullOrWhiteSpace(options.TmdbToken) && string.IsNullOrWhiteSpace(options.TvdbKey) &&
                string.IsNullOrWhiteSpace(options.OmdbKey)) return result;

            var needsDate = !item.PremiereDate.HasValue;
            var needsOverview = string.IsNullOrWhiteSpace(item.Overview);
            var needsRating = !item.CommunityRating.HasValue;
            var needsPoster = !item.HasImage(ImageType.Primary);
            var needsBackdrop = !item.HasImage(ImageType.Backdrop);
            if (!needsDate && !needsOverview && !needsRating && !needsPoster && !needsBackdrop)
            {
                return result;
            }

            await Lookups.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var data = await Client.GetDetails(options, series, item.Name, item.ProductionYear,
                    GetId(item, "Imdb"), GetId(item, "Tmdb"), GetId(item, "Tvdb"), token).ConfigureAwait(false);
                if (needsDate && data.PremiereDate.HasValue)
                {
                    item.PremiereDate = data.PremiereDate;
                    item.ProductionYear = item.ProductionYear ?? data.PremiereDate.Value.Year;
                    result |= ItemUpdateType.MetadataDownload;
                }
                if (needsOverview && data.Overview != null) { item.Overview = data.Overview; result |= ItemUpdateType.MetadataDownload; }
                if (needsRating && data.Rating.HasValue) { item.CommunityRating = data.Rating; result |= ItemUpdateType.MetadataDownload; }
                if (string.IsNullOrWhiteSpace(item.OfficialRating) && data.OfficialRating != null)
                {
                    item.OfficialRating = data.OfficialRating;
                    result |= ItemUpdateType.MetadataDownload;
                }
                if (SetId(item, "Imdb", data.Imdb) | SetId(item, "Tmdb", data.Tmdb) | SetId(item, "Tvdb", data.Tvdb))
                {
                    result |= ItemUpdateType.MetadataDownload;
                }
                if (needsPoster && SetImage(item, ImageType.Primary, data.Poster))
                {
                    result |= ItemUpdateType.ImageUpdate;
                }
                if (needsBackdrop && SetImage(item, ImageType.Backdrop, data.Backdrop))
                {
                    result |= ItemUpdateType.ImageUpdate;
                }
                return result | ApplyEpg(item, epg);
            }
            finally { Lookups.Release(); }
        }

        internal static ItemUpdateType ApplyEpg(BaseItem item, EpgIndex index)
        {
            if (index == null || item.IsLocked) return ItemUpdateType.None;
            var record = index.Match(item.Name, item.ProductionYear, GetId(item, "Imdb"), GetId(item, "Tmdb"));
            if (record == null) return ItemUpdateType.None;
            var result = ItemUpdateType.None;
            if (record.Overview != null && item.Overview != record.Overview && !item.IsFieldLocked(MetadataFields.Overview))
            {
                item.Overview = record.Overview;
                result |= ItemUpdateType.MetadataDownload;
            }
            if (record.OfficialRating != null && item.OfficialRating != record.OfficialRating &&
                !item.IsFieldLocked(MetadataFields.OfficialRating))
            {
                item.OfficialRating = record.OfficialRating;
                result |= ItemUpdateType.MetadataDownload;
            }
            if (record.Rating.HasValue && item.CommunityRating != record.Rating && !item.IsFieldLocked(MetadataFields.CommunityRating))
            {
                item.CommunityRating = record.Rating;
                result |= ItemUpdateType.MetadataDownload;
            }
            if (record.Date.HasValue && item.PremiereDate != record.Date)
            {
                item.PremiereDate = record.Date;
                item.ProductionYear = record.Date.Value.Year;
                result |= ItemUpdateType.MetadataDownload;
            }
            else if (!item.ProductionYear.HasValue && record.Year.HasValue)
            {
                item.ProductionYear = record.Year;
                result |= ItemUpdateType.MetadataDownload;
            }
            if (record.Poster != null && item.PrimaryImagePath != record.Poster && SetImage(item, ImageType.Primary, record.Poster))
                result |= ItemUpdateType.ImageUpdate;
            return result;
        }

        private static bool SetImage(BaseItem item, ImageType type, string url)
        {
            if (url == null) return false;
            item.SetImage(new ItemImageInfo { Path = url, Type = type, DateModified = DateTimeOffset.UtcNow }, 0);
            return true;
        }

        private static bool IsVod(BaseItem item) =>
            item.ProviderIds.ContainsKey(VideoOnDemandChannel.ProviderMarker) ||
            string.Equals(item.FindParent<Channel>()?.Name, "Video On Demand", StringComparison.Ordinal);

        private static string GetId(BaseItem item, string key) =>
            item.ProviderIds.TryGetValue(key, out var id) ? id : null;

        private static bool SetId(BaseItem item, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || item.ProviderIds.ContainsKey(key)) return false;
            item.ProviderIds[key] = value;
            return true;
        }
    }
}
