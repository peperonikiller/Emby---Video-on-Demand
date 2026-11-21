using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;

using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace EmbyVideoOnDemand
{
    public sealed class VideoOnDemandChannel : IChannel, IRequiresMediaInfoCallback
    {
        private static readonly Regex EpisodePattern = new Regex(
            @"(?<![\p{L}\p{N}])S(?<season>\d{1,3})\s*[. _-]*E(?<episode>\d{1,4})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ImdbId = new Regex(@"^tt\d{7,12}$", RegexOptions.CultureInvariant);
        private readonly SemaphoreSlim _catalogLock = new SemaphoreSlim(1);
        private string _catalogUrl;
        private DateTimeOffset _expires;
        private Catalog _catalog;
        private string _playlistHash;
        private int _refreshRequest;
        private int _catalogRequest;

        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1);
        private CatalogRefreshStatus _status = new CatalogRefreshStatus("Idle");
        private IReadOnlyDictionary<string, string> _priorPlayback;

        internal CatalogRefreshStatus RefreshStatus(string stampPath)
        {
            var status = Volatile.Read(ref _status).Copy();
            status.LastSuccessfulRefresh = File.Exists(stampPath)
                ? (DateTimeOffset?)File.GetLastWriteTimeUtc(stampPath) : null;
            return status;
        }

        internal void QueueCatalogRefresh(IChannelManager manager, string stampPath, string previousSource)
        {
            Interlocked.Increment(ref _refreshRequest);
            _ = Task.Run(async () =>
            {
                await _refreshLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var source = Plugin.Instance?.CurrentOptions().VodPlaylistUrl;
                    Volatile.Write(ref _status, new CatalogRefreshStatus("Checking playlist"));
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        await manager.RefreshChannelContent(this, int.MaxValue, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (File.Exists(stampPath))
                        {
                            File.Delete(stampPath);
                        }
                        Volatile.Write(ref _status, new CatalogRefreshStatus("Playlist removed"));
                        return;
                    }

                    if (_catalog == null && !string.IsNullOrWhiteSpace(previousSource) &&
                        !string.Equals(source.Trim(), previousSource, StringComparison.Ordinal))
                    {
                        try
                        {
                            await GetCatalog(previousSource, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException) { /* A new source can repair an unavailable old source. */ }
                    }
                    _priorPlayback = _priorPlayback ?? _catalog?.Playback;
                    var catalog = await GetCatalog(source, CancellationToken.None, allowStale: false).ConfigureAwait(false);
                    var previous = File.Exists(stampPath) ? File.ReadAllLines(stampPath) : Array.Empty<string>();
                    var hashes = catalog.IndexHashes;
                    if (previous.SequenceEqual(hashes))
                    {
                        Volatile.Write(ref _status, StatusFor("No catalog changes", catalog));
                        _priorPlayback = null;
                        return;
                    }

                    // The native API uses the external folder ID to limit the walk to one section.
                    string folder = null;
                    if (previous.Length == 2)
                    {
                        if (previous[0] == hashes[0]) folder = "tv";
                        else if (previous[1] == hashes[1]) folder = "movies";
                    }
                    Volatile.Write(ref _status, StatusFor(folder == "tv" ? "Indexing TV shows" :
                        folder == "movies" ? "Indexing movies" : "Indexing catalog", catalog));
                    await manager.RefreshChannelContent(this, int.MaxValue, folder, CancellationToken.None)
                        .ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(stampPath));
                    File.WriteAllLines(stampPath, hashes);
                    _priorPlayback = null;
                    Volatile.Write(ref _status, StatusFor("Complete", catalog));
                }
                catch (Exception ex)
                {
                    // Feed URLs and provider credentials can appear in exception messages.
                    Console.Error.WriteLine("Video On Demand catalog refresh failed (" + ex.GetType().Name + ").");
                    Volatile.Write(ref _status, new CatalogRefreshStatus("Refresh failed"));
                }
                finally { _refreshLock.Release(); }
            });
        }

        public string Name => "Video On Demand";
        public string Description => "Browse and search the movies and TV shows available in your M3U.";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.Adult;
        public IEnumerable<ImageType> GetSupportedChannelImages() =>
            new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new DynamicImageResponse();
            if (type == ImageType.Primary || type == ImageType.Banner || type == ImageType.Thumb)
            {
                response.Stream = Plugin.OpenBanner();
                response.Format = ImageFormat.Jpg;
            }
            return Task.FromResult(response);
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            var source = Plugin.Instance?.CurrentOptions().VodPlaylistUrl;
            if (string.IsNullOrWhiteSpace(source))
            {
                return Page(Array.Empty<ChannelItemInfo>(), query);
            }
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return Page(new[] { Folder("movies", "Movies"), Folder("tv", "TV Shows") }, query);
            }

            var catalog = await GetCatalog(source, cancellationToken).ConfigureAwait(false);
            return Page(catalog.Folders.TryGetValue(query.FolderId, out var items)
                ? items : Array.Empty<ChannelItemInfo>(), query);
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var source = Plugin.Instance?.CurrentOptions().VodPlaylistUrl;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrEmpty(id))
            {
                return Array.Empty<MediaSourceInfo>();
            }

            string url;
            try
            {
                var catalog = await GetCatalog(source, cancellationToken).ConfigureAwait(false);
                if (!catalog.Playback.TryGetValue(id, out url))
                {
                    _priorPlayback?.TryGetValue(id, out url);
                }
            }
            catch (InvalidOperationException) when (_priorPlayback != null)
            {
                url = _priorPlayback != null && _priorPlayback.TryGetValue(id, out var oldUrl) ? oldUrl : null;
            }
            return url != null ? new[]
            {
                new MediaSourceInfo
                {
                    Id = id, Path = url, Protocol = MediaProtocol.Http, IsRemote = true,
                    SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true
                }
            } : Array.Empty<MediaSourceInfo>();
        }

        private async Task<Catalog> GetCatalog(string source, CancellationToken cancellationToken, bool allowStale = true)
        {
            var feed = source.Trim();
            await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var request = Volatile.Read(ref _refreshRequest);
                if (_catalog != null && _catalogUrl == feed && _catalogRequest == request &&
                    DateTimeOffset.UtcNow < _expires)
                {
                    return _catalog;
                }
                try
                {
                    if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    {
                        throw new InvalidDataException();
                    }
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(90));
                        var m3u = await Playlist.ReadAsync(feed, timeout.Token).ConfigureAwait(false);
                        using (var hash = SHA256.Create())
                        {
                            var fingerprint = StableId(hash, m3u);
                            if (_catalog == null || _catalogUrl != feed || _playlistHash != fingerprint)
                            {
                                _catalog = BuildCatalog(m3u, timeout.Token);
                                _playlistHash = fingerprint;
                            }
                        }
                    }
                    _catalogUrl = feed;
                    _catalogRequest = request;
                    _expires = DateTimeOffset.UtcNow.AddMinutes(30);
                }
                catch (Exception) when (allowStale && !cancellationToken.IsCancellationRequested)
                {
                    if (_catalog == null || _catalogUrl != feed)
                    {
                        throw new InvalidOperationException("Could not read the Video On Demand M3U playlist.");
                    }
                    _catalogRequest = request;
                    _expires = DateTimeOffset.UtcNow.AddMinutes(2);
                }
                return _catalog;
            }
            finally
            {
                _catalogLock.Release();
            }
        }

        private static CatalogRefreshStatus StatusFor(string phase, Catalog catalog) =>
            new CatalogRefreshStatus(phase, catalog.Folders["movies"].Length, catalog.Folders["tv"].Length,
                catalog.Playback.Count - catalog.Folders["movies"].Length);

        public sealed class CatalogRefreshStatus
        {
            internal CatalogRefreshStatus(string phase, int? movies = null, int? shows = null, int? episodes = null)
            {
                Phase = phase;
                Movies = movies;
                Shows = shows;
                Episodes = episodes;
            }

            internal CatalogRefreshStatus Copy() => new CatalogRefreshStatus(Phase, Movies, Shows, Episodes);
            public string Phase { get; }
            public int? Movies { get; }
            public int? Shows { get; }
            public int? Episodes { get; }
            public DateTimeOffset? LastSuccessfulRefresh { get; set; }
        }

        private static Catalog BuildCatalog(string text, CancellationToken cancellationToken)
        {
            var catalog = new Catalog();
            var movies = new Dictionary<string, ChannelItemInfo>();
            var series = new Dictionary<string, ChannelItemInfo>();
            var seasons = new Dictionary<string, Dictionary<int, ChannelItemInfo>>();
            var episodes = new Dictionary<string, Dictionary<int, ChannelItemInfo>>();
            using (var hash = SHA256.Create())
            {
                foreach (var entry in Playlist.Parse(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var location) ||
                        (location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps))
                    {
                        continue;
                    }

                    var isMovie = string.Equals(entry.MediaKind, "movie", StringComparison.OrdinalIgnoreCase);
                    var isSeries = string.Equals(entry.MediaKind, "series", StringComparison.OrdinalIgnoreCase);
                    var episode = isMovie ? Match.Empty : EpisodePattern.Match(entry.Title);
                    var name = Playlist.DisplayTitle(episode.Success ? entry.Title.Substring(0, episode.Index) : entry.Title);
                    var title = Playlist.NormalizeTitle(name);
                    if (string.IsNullOrWhiteSpace(title.Item1))
                    {
                        continue;
                    }

                    if (title.Item2.HasValue && name.EndsWith("(" + title.Item2 + ")", StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - 6).TrimEnd();
                    }
                    var imdb = ImdbId.IsMatch(entry.ProviderId ?? "") ? entry.ProviderId : null;
                    var artwork = TrustedArtwork(entry.Artwork);
                    var titleKey = title.Item1 + ":" + title.Item2;
                    if (!episode.Success)
                    {
                        if (isSeries)
                        {
                            continue;
                        }
                        var id = "movie:" + StableId(hash, imdb ?? titleKey);
                        if (!movies.ContainsKey(id))
                        {
                            var item = Video(id, name, ChannelMediaContentType.Movie, artwork);
                            item.ProductionYear = title.Item2;
                            if (imdb != null)
                            {
                                item.ProviderIds = new ProviderIdDictionary { ["Imdb"] = imdb };
                            }
                            MarkTitle(item);
                            movies.Add(id, item);
                            catalog.Playback.Add(id, entry.Url);
                        }
                        continue;
                    }

                    var tmdb = isSeries && int.TryParse(entry.ProviderId, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId)
                        && seriesId > 0 ? seriesId.ToString(CultureInfo.InvariantCulture) : null;
                    var episodePrefix = "show:" + StableId(hash, tmdb ?? imdb ?? titleKey);
                    // Generic channel folders open a list instead of invoking the client's series play action.
                    var showId = "browse:" + episodePrefix;
                    if (!series.ContainsKey(showId))
                    {
                        var show = Folder(showId, name);
                        show.ImageUrl = artwork;
                        show.ProductionYear = title.Item2;
                        if (tmdb != null || imdb != null)
                        {
                            show.ProviderIds = new ProviderIdDictionary { [tmdb != null ? "Tmdb" : "Imdb"] = tmdb ?? imdb };
                        }
                        MarkTitle(show);
                        series.Add(showId, show);
                        seasons.Add(showId, new Dictionary<int, ChannelItemInfo>());
                    }
                    var seasonNumber = int.Parse(episode.Groups["season"].Value, CultureInfo.InvariantCulture);
                    var episodeNumber = int.Parse(episode.Groups["episode"].Value, CultureInfo.InvariantCulture);
                    var seasonId = showId + ":" + seasonNumber;
                    if (!seasons[showId].ContainsKey(seasonNumber))
                    {
                        var season = Folder(seasonId, seasonNumber == 0 ? "Specials" : $"Season {seasonNumber:00}");
                        season.ImageUrl = series[showId].ImageUrl;
                        season.IndexNumber = seasonNumber;
                        MarkTitle(season);
                        seasons[showId].Add(seasonNumber, season);
                        episodes.Add(seasonId, new Dictionary<int, ChannelItemInfo>());
                    }
                    if (!episodes[seasonId].ContainsKey(episodeNumber))
                    {
                        var id = $"{episodePrefix}:{seasonNumber}:{episodeNumber}";
                        var label = $"{series[showId].Name} S{seasonNumber:00}E{episodeNumber:00}";
                        var item = Video(id, label, ChannelMediaContentType.Episode, null);
                        item.SeriesName = series[showId].Name;
                        item.IndexNumber = episodeNumber;
                        item.ParentIndexNumber = seasonNumber;
                        episodes[seasonId].Add(episodeNumber, item);
                        catalog.Playback.Add(id, entry.Url);
                    }
                }
            }

            if (catalog.Playback.Count == 0)
            {
                throw new InvalidDataException();
            }
            catalog.Folders["movies"] = movies.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            catalog.Folders["tv"] = series.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var folder in seasons.Concat(episodes))
            {
                catalog.Folders[folder.Key] = folder.Value.OrderBy(item => item.Key).Select(item => item.Value).ToArray();
            }
            catalog.IndexHashes = new[] { HashSection(catalog, false), HashSection(catalog, true) };
            return catalog;
        }

        private static string HashSection(Catalog catalog, bool tv)
        {
            var fields = new StringBuilder();
            foreach (var folder in catalog.Folders.Where(pair => tv ? pair.Key != "movies" : pair.Key == "movies")
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                fields.AppendLine(folder.Key);
                foreach (var item in folder.Value.OrderBy(item => item.Id, StringComparer.Ordinal))
                {
                    fields.Append(item.Id).Append('\t').Append(item.Name).Append('\t')
                        .Append(item.ProductionYear).Append('\t').AppendLine(item.ImageUrl);
                }
            }
            using (var hash = SHA256.Create())
            {
                return StableId(hash, fields.ToString());
            }
        }

        internal const string ProviderMarker = "EmbyVod";

        private static void MarkTitle(ChannelItemInfo item)
        {
            if (item.ProviderIds == null)
            {
                item.ProviderIds = new ProviderIdDictionary();
            }
            item.ProviderIds[ProviderMarker] = item.Id;
        }

        internal static string TrustedArtwork(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0)
            {
                var isTmdb = uri.Host == "image.tmdb.org" && uri.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal);
                var isFeedArtwork = uri.Host == "img.pia-games.com" &&
                    uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
                if (isTmdb || isFeedArtwork)
                {
                    return uri.GetLeftPart(UriPartial.Path);
                }
            }
            return null;
        }

        private static string StableId(SHA256 hash, string key) =>
            BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "");

        private static ChannelItemInfo Folder(string id, string name, ChannelFolderType type = ChannelFolderType.Container) =>
            new ChannelItemInfo { Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = type };

        private static ChannelItemInfo Video(string id, string name, ChannelMediaContentType kind, string artwork) =>
            new ChannelItemInfo { Id = id, Name = name, Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video, ContentType = kind, ImageUrl = artwork };

        private static ChannelItemResult Page(ChannelItemInfo[] items, InternalChannelItemQuery query)
        {
            return new ChannelItemResult
            {
                TotalRecordCount = items.Length,
                Items = items.Skip(query.StartIndex ?? 0).Take(query.Limit ?? items.Length).ToList()
            };
        }

        private sealed class Catalog
        {
            public string[] IndexHashes { get; set; }
            public Dictionary<string, ChannelItemInfo[]> Folders { get; } =
                new Dictionary<string, ChannelItemInfo[]>(StringComparer.Ordinal);
            public Dictionary<string, string> Playback { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
