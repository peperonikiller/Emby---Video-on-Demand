using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyVideoOnDemand
{
    internal sealed class MetadataClient
    {
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _tvdbLogin = new SemaphoreSlim(1);
        private string _tvdbToken;
        private string _tvdbKey;
        private string _tvdbPin;
        private DateTimeOffset _tvdbExpiry;

        public MetadataClient(HttpClient http) { _http = http; }

        public async Task<Details> GetDetails(Options options, bool series, string name, int? year,
            string imdb, string tmdb, string tvdb, CancellationToken token)
        {
            var result = new Details { Imdb = imdb, Tmdb = tmdb, Tvdb = tvdb };
            if (!string.IsNullOrWhiteSpace(options.TmdbToken))
            {
                await TryProvider(async () =>
                {
                    var key = options.TmdbToken.Trim();
                    var type = series ? "tv" : "movie";
                    if (!PositiveId(result.Tmdb) && IsImdb(result.Imdb))
                    {
                        var found = await Get<TmdbResults>("https://api.themoviedb.org/3/find/" + result.Imdb +
                            "?external_source=imdb_id", key, token).ConfigureAwait(false);
                        result.Tmdb = (series ? found?.Series : found?.Movies)?.SingleOrDefault()?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                    if (!PositiveId(result.Tmdb) && !IsImdb(result.Imdb))
                    {
                        var search = await Get<TmdbResults>("https://api.themoviedb.org/3/search/" + type +
                            "?query=" + Uri.EscapeDataString(name ?? "") + "&include_adult=false", key, token).ConfigureAwait(false);
                        result.Tmdb = Unique(search?.Results, row => series ? row.Name : row.Title,
                            row => ParseYear(series ? row.FirstAirDate : row.ReleaseDate), name, year)?.Id.ToString(CultureInfo.InvariantCulture);
                    }
                    if (!PositiveId(result.Tmdb))
                    {
                        return;
                    }
                    var data = await Get<TmdbTitle>("https://api.themoviedb.org/3/" + type + "/" + result.Tmdb +
                        "?append_to_response=external_ids", key, token).ConfigureAwait(false);
                    if (data == null)
                    {
                        return;
                    }
                    result.PremiereDate = ParseDate(series ? data.FirstAirDate : data.ReleaseDate);
                    result.Overview = Text(data.Overview);
                    result.Poster = TmdbImage(data.Poster);
                    result.Backdrop = TmdbImage(data.Backdrop);
                    result.Rating = Rating(data.Rating);
                    result.Imdb = result.Imdb ?? data.External?.Imdb ?? data.Imdb;
                    if (data.External?.Tvdb > 0)
                    {
                        result.Tvdb = data.External.Tvdb.Value.ToString(CultureInfo.InvariantCulture);
                    }
                }, token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.TvdbKey))
            {
                await TryProvider(async () =>
                {
                    var auth = await GetTvdbToken(options, token).ConfigureAwait(false);
                    if (auth == null)
                    {
                        return;
                    }
                    var type = series ? "series" : "movie";
                    if (!PositiveId(result.Tvdb) && IsImdb(result.Imdb))
                    {
                        var found = await Get<Envelope<TvdbRemote[]>>("https://api4.thetvdb.com/v4/search/remoteid/" +
                            result.Imdb, auth, token).ConfigureAwait(false);
                        var ids = (found?.Data ?? Array.Empty<TvdbRemote>()).Select(row => series ? row.Series : row.Movie)
                            .Where(row => row != null && row.Id > 0).Select(row => row.Id).Distinct().Take(2).ToArray();
                        if (ids.Length == 1) result.Tvdb = ids[0].ToString(CultureInfo.InvariantCulture);
                    }
                    if (!PositiveId(result.Tvdb) && !IsImdb(result.Imdb))
                    {
                        var found = await Get<Envelope<TvdbSearch[]>>("https://api4.thetvdb.com/v4/search?type=" +
                            type + "&query=" + Uri.EscapeDataString(name ?? ""), auth, token).ConfigureAwait(false);
                        result.Tvdb = Unique(found?.Data, row => row.Name,
                            row => ParseYear(row.Year), name, year)?.Id;
                    }
                    if (!PositiveId(result.Tvdb)) return;
                    var path = "https://api4.thetvdb.com/v4/" + (series ? "series/" : "movies/") + result.Tvdb;
                    var entry = (await Get<Envelope<TvdbTitle>>(path + "/extended", auth, token).ConfigureAwait(false))?.Data;
                    if (entry == null) return;
                    result.PremiereDate = result.PremiereDate ?? ParseDate(entry.FirstAired);
                    result.Poster = result.Poster ?? TrustedImage(entry.Image, "artworks.thetvdb.com");
                    result.OfficialRating = entry.ContentRatings?.FirstOrDefault(row =>
                        string.Equals(row.Country, "usa", StringComparison.OrdinalIgnoreCase))?.Name;
                    if (result.Overview == null)
                    {
                        var translation = await Get<Envelope<TvdbTitle>>(path + "/translations/eng", auth, token).ConfigureAwait(false);
                        result.Overview = Text(translation?.Data?.Overview) ?? Text(entry.Overview);
                    }
                }, token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.OmdbKey) && IsImdb(result.Imdb))
            {
                await TryProvider(async () =>
                {
                    var data = await Get<OmdbTitle>("https://www.omdbapi.com/?i=" + result.Imdb +
                        "&plot=full&apikey=" + Uri.EscapeDataString(options.OmdbKey.Trim()), null, token).ConfigureAwait(false);
                    result.PremiereDate = result.PremiereDate ?? ParseDate(data?.Released);
                    result.Overview = result.Overview ?? Text(data?.Plot);
                    result.Poster = result.Poster ?? TrustedImage(data?.Poster, "m.media-amazon.com", "ia.media-imdb.com");
                    result.OfficialRating = result.OfficialRating ?? Text(data?.Rated);
                    if (result.Rating == null && float.TryParse(data?.Rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        result.Rating = Rating(value);
                    }
                }, token).ConfigureAwait(false);
            }
            return result;
        }

        public async Task<Details> GetSeason(string token, string seriesId, int season, CancellationToken cancellationToken)
        {
            var result = new Details();
            if (string.IsNullOrWhiteSpace(token) || !PositiveId(seriesId) || season < 0)
            {
                return result;
            }
            await TryProvider(async () =>
            {
                var path = "https://api.themoviedb.org/3/tv/" + seriesId + "/season/" +
                    season.ToString(CultureInfo.InvariantCulture);
                var details = await Get<TmdbTitle>(path, token.Trim(), cancellationToken).ConfigureAwait(false);
                result.Poster = TmdbImage(details?.Poster);
                result.Overview = Text(details?.Overview);
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task<string> GetTvdbToken(Options options, CancellationToken token)
        {
            var key = options.TvdbKey.Trim();
            var pin = options.TvdbPin?.Trim();
            await _tvdbLogin.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_tvdbKey == key && _tvdbPin == pin && DateTimeOffset.UtcNow < _tvdbExpiry)
                {
                    return _tvdbToken;
                }
                var payload = new TvdbLogin { Key = key, Pin = string.IsNullOrWhiteSpace(pin) ? null : pin };
                var json = new System.IO.MemoryStream();
                using (json)
                {
                    new DataContractJsonSerializer(typeof(TvdbLogin)).WriteObject(json, payload);
                    using (var body = new StringContent(Encoding.UTF8.GetString(json.ToArray()), Encoding.UTF8, "application/json"))
                    {
                        var login = await Request<Envelope<TvdbToken>>(HttpMethod.Post, "https://api4.thetvdb.com/v4/login",
                            null, body, token).ConfigureAwait(false);
                        _tvdbToken = login?.Data?.Token;
                    }
                }
                _tvdbKey = key;
                _tvdbPin = pin;
                _tvdbExpiry = DateTimeOffset.UtcNow.Add(_tvdbToken == null ? TimeSpan.FromMinutes(5) : TimeSpan.FromDays(20));
                return _tvdbToken;
            }
            finally { _tvdbLogin.Release(); }
        }

        private Task<T> Get<T>(string url, string bearer, CancellationToken token) where T : class
            => Request<T>(HttpMethod.Get, url, bearer, null, token);

        private async Task<T> Request<T>(HttpMethod method, string url, string bearer, HttpContent content,
            CancellationToken token) where T : class
        {
            if (url.StartsWith("https://api.themoviedb.org/", StringComparison.Ordinal) && bearer != null &&
                System.Text.RegularExpressions.Regex.IsMatch(bearer, @"^[A-Fa-f0-9]{32}$"))
            {
                url += (url.Contains("?") ? "&" : "?") + "api_key=" + bearer;
                bearer = null;
            }
            using (var message = new HttpRequestMessage(method, url) { Content = content })
            {
                if (!string.IsNullOrWhiteSpace(bearer))
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                using (var response = await _http.SendAsync(message, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                    }
                }
            }
        }

        private static async Task TryProvider(Func<Task> action, CancellationToken token)
        {
            try { await action().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            catch (SerializationException) { }
            catch (System.Xml.XmlException) { }
            catch (FormatException) { }
            catch (InvalidOperationException) { }
        }

        private static T Unique<T>(IEnumerable<T> items, Func<T, string> title, Func<T, int?> year,
            string desired, int? desiredYear) where T : class
        {
            if (string.IsNullOrWhiteSpace(desired)) return null;
            var key = Playlist.NormalizeTitle(desired).Item1;
            var matches = (items ?? Enumerable.Empty<T>()).Where(item =>
                Playlist.NormalizeTitle(title(item) ?? "").Item1 == key &&
                (!desiredYear.HasValue || year(item) == desiredYear)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static DateTimeOffset? ParseDate(string value)
        {
            return DateTimeOffset.TryParseExact(value, new[] { "yyyy-MM-dd", "dd MMM yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date.Year > 1800
                ? date : (DateTimeOffset?)null;
        }

        private static int? ParseYear(string value) => value?.Length >= 4 &&
            int.TryParse(value.Substring(0, 4), out var year) ? year : (int?)null;
        private static bool PositiveId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
        private static bool IsImdb(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^tt[0-9]{7,12}$");
        private static float? Rating(float value) => value > 0 && value <= 10 ? value : (float?)null;
        private static string Text(string value) => string.IsNullOrWhiteSpace(value) || value == "N/A" ? null : value.Trim();
        private static string TmdbImage(string path) => !string.IsNullOrEmpty(path) && path[0] == '/' &&
            System.Text.RegularExpressions.Regex.IsMatch(path, @"^/[A-Za-z0-9_.-]+$") ? "https://image.tmdb.org/t/p/original" + path : null;
        private static string TrustedImage(string image, params string[] hosts) =>
            Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0 && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                ? uri.AbsoluteUri : null;

        internal sealed class Details
        {
            public string Imdb, Tmdb, Tvdb, Overview, Poster, Backdrop, OfficialRating;
            public float? Rating;
            public DateTimeOffset? PremiereDate;
        }

        [DataContract] private sealed class TmdbResults
        {
            [DataMember(Name = "results")] public TmdbTitle[] Results { get; set; }
            [DataMember(Name = "movie_results")] public TmdbTitle[] Movies { get; set; }
            [DataMember(Name = "tv_results")] public TmdbTitle[] Series { get; set; }
        }
        [DataContract] private sealed class TmdbTitle
        {
            [DataMember(Name = "id")] public int Id { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "title")] public string Title { get; set; }
            [DataMember(Name = "release_date")] public string ReleaseDate { get; set; }
            [DataMember(Name = "first_air_date")] public string FirstAirDate { get; set; }
            [DataMember(Name = "overview")] public string Overview { get; set; }
            [DataMember(Name = "poster_path")] public string Poster { get; set; }
            [DataMember(Name = "backdrop_path")] public string Backdrop { get; set; }
            [DataMember(Name = "vote_average")] public float Rating { get; set; }
            [DataMember(Name = "imdb_id")] public string Imdb { get; set; }
            [DataMember(Name = "external_ids")] public ExternalIds External { get; set; }
        }
        [DataContract] private sealed class ExternalIds
        {
            [DataMember(Name = "imdb_id")] public string Imdb { get; set; }
            [DataMember(Name = "tvdb_id")] public int? Tvdb { get; set; }
        }
        [DataContract] private sealed class Envelope<T>
        {
            [DataMember(Name = "data")] public T Data { get; set; }
        }
        [DataContract] private sealed class TvdbLogin
        {
            [DataMember(Name = "apikey")] public string Key { get; set; }
            [DataMember(Name = "pin", EmitDefaultValue = false)] public string Pin { get; set; }
        }
        [DataContract] private sealed class TvdbToken
        {
            [DataMember(Name = "token")] public string Token { get; set; }
        }
        [DataContract] private sealed class TvdbSearch
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "tvdb_id")] public string Id { get; set; }
            [DataMember(Name = "year")] public string Year { get; set; }
        }
        [DataContract] private sealed class TvdbRemote
        {
            [DataMember(Name = "series")] public TvdbTitle Series { get; set; }
            [DataMember(Name = "movie")] public TvdbTitle Movie { get; set; }
        }
        [DataContract] private sealed class TvdbTitle
        {
            [DataMember(Name = "id")] public int Id { get; set; }
            [DataMember(Name = "image")] public string Image { get; set; }
            [DataMember(Name = "overview")] public string Overview { get; set; }
            [DataMember(Name = "firstAired")] public string FirstAired { get; set; }
            [DataMember(Name = "contentRatings")] public TvdbRating[] ContentRatings { get; set; }
        }
        [DataContract] private sealed class TvdbRating
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "country")] public string Country { get; set; }
        }
        [DataContract] private sealed class OmdbTitle
        {
            [DataMember(Name = "Released")] public string Released { get; set; }
            [DataMember(Name = "Plot")] public string Plot { get; set; }
            [DataMember(Name = "Poster")] public string Poster { get; set; }
            [DataMember(Name = "Rated")] public string Rated { get; set; }
            [DataMember(Name = "imdbRating")] public string Rating { get; set; }
        }
    }
}
