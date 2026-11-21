using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace EmbyVideoOnDemand
{
    internal sealed class EpgIndex
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1);
        private static readonly Regex Imdb = new Regex(@"^tt[0-9]{7,12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Dictionary<string, Record> _ids = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        private ILookup<string, Record> _titles;
        private static EpgIndex _cached;
        private static string _source;
        private static DateTime _refreshAfter;

        internal static async Task<EpgIndex> Get(string source, CancellationToken token, bool force = false)
        {
            if (!Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.UserInfo.Length != 0)
                return null;
            await RefreshLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var sameSource = _source == uri.AbsoluteUri;
                if (!force && sameSource && DateTime.UtcNow < _refreshAfter) return _cached;
                var previous = sameSource ? _cached : null;
                _source = uri.AbsoluteUri;
                _cached = previous;
                _refreshAfter = DateTime.UtcNow.AddMinutes(10);
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(2));
                        using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                        {
                            request.Headers.UserAgent.ParseAdd("EmbyVideoOnDemand/1.0");
                            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                                timeout.Token).ConfigureAwait(false))
                            {
                                response.EnsureSuccessStatusCode();
                                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (timeout.Token.Register(stream.Dispose))
                                {
                                    var contentType = response.Content.Headers.ContentType?.MediaType;
                                    if (contentType == "application/gzip" || contentType == "application/x-gzip" ||
                                        response.RequestMessage.RequestUri.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                                    {
                                        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                                            _cached = Read(gzip, timeout.Token, previous);
                                    }
                                    else _cached = Read(stream, timeout.Token, previous);
                                }
                            }
                        }
                    }
                    _refreshAfter = DateTime.UtcNow.AddHours(_cached?.Complete == true ? 12 : 1);
                }
                catch (HttpRequestException) { ReportUnavailable(); }
                catch (IOException) when (!token.IsCancellationRequested) { ReportUnavailable(); }
                catch (ObjectDisposedException) when (!token.IsCancellationRequested) { ReportUnavailable(); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { ReportUnavailable(); }
                token.ThrowIfCancellationRequested();
                return _cached;
            }
            finally { RefreshLock.Release(); }
        }

        private static void ReportUnavailable() => Console.Error.WriteLine("Video On Demand could not refresh EPG metadata.");

        internal bool Complete { get; private set; }

        internal static EpgIndex Read(Stream stream, CancellationToken token, EpgIndex previous = null)
        {
            var index = new EpgIndex();
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 128L * 1024 * 1024
            }))
            {
                try
                {
                    if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "tv") return previous;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.Depth != 1 || reader.NodeType != XmlNodeType.Element ||
                            (reader.LocalName != "channel" && reader.LocalName != "programme")) continue;
                        using (var subtree = reader.ReadSubtree())
                            index.Add(XElement.Load(subtree));
                    }
                    index.Complete = index._ids.Count > 0;
                }
                catch (XmlException)
                {
                    Console.Error.WriteLine("Video On Demand received incomplete or invalid EPG XML.");
                    if (previous?.Complete == true) return previous;
                }
            }
            if (index._ids.Count == 0) return previous;
            index._titles = index._ids.Values.Where(record => !record.Ambiguous && record.Title.Length > 0)
                .ToLookup(record => record.Title, StringComparer.Ordinal);
            return index;
        }

        private void Add(XElement row)
        {
            var channel = row.Name.LocalName == "channel";
            var id = (string)row.Attribute(channel ? "id" : "channel");
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!channel && (Field(row, "sub-title") != null || row.Elements().Any(e => e.Name.LocalName == "episode-num" &&
                (string)e.Attribute("system") == "xmltv_ns"))) return;
            var name = Field(row, channel ? "display-name" : "title");
            if (name == null || Regex.IsMatch(name, @"\bS\d+\s*E\d+\b", RegexOptions.IgnoreCase)) return;
            var title = Playlist.NormalizeTitle(name);
            if (!_ids.TryGetValue(id, out var record))
                _ids[id] = record = new Record
                {
                    Title = title.Item1,
                    Year = title.Item2,
                    Imdb = Imdb.IsMatch(id) ? id : null
                };
            if (record.Title != title.Item1 || (record.Year.HasValue && title.Item2.HasValue && record.Year != title.Item2))
            {
                record.Ambiguous = true;
                return;
            }
            record.Year = record.Year ?? title.Item2;
            record.Poster = record.Poster ?? VideoOnDemandChannel.TrustedArtwork(
                (string)row.Elements().FirstOrDefault(e => e.Name.LocalName == "icon")?.Attribute("src"));
            record.Overview = record.Overview ?? Field(row, "desc");
            var dateText = Field(row, "date");
            if (!record.Date.HasValue && DateTimeOffset.TryParseExact(dateText,
                new[] { "yyyyMMdd", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                record.Date = date;
            if (record.Year == null && dateText?.Length == 4 && int.TryParse(dateText, out var year) && year >= 1800)
                record.Year = year;
            var rating = row.Elements().FirstOrDefault(e => e.Name.LocalName == "rating");
            record.OfficialRating = record.OfficialRating ?? (rating == null ? null : Field(rating, "value"));
            var stars = row.Elements().FirstOrDefault(e => e.Name.LocalName == "star-rating");
            var parts = (stars == null ? null : Field(stars, "value"))?.Split('/');
            if (!record.Rating.HasValue && parts?.Length == 2 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) &&
                value >= 0 && value <= scale && scale > 0 && !float.IsInfinity(scale))
                record.Rating = value / scale * 10;
        }

        internal Record Match(string name, int? year, string imdb, string tmdb)
        {
            if (imdb != null && Imdb.IsMatch(imdb) && _ids.TryGetValue(imdb, out var exact) && !exact.Ambiguous)
                return exact;
            var title = Playlist.NormalizeTitle(name ?? "");
            year = year ?? title.Item2;
            if (tmdb != null && _ids.TryGetValue(tmdb, out var numeric) && !numeric.Ambiguous &&
                numeric.Title == title.Item1 && (!year.HasValue || !numeric.Year.HasValue || numeric.Year == year))
                return numeric;
            if (!Complete || !year.HasValue) return null;
            var matches = _titles[title.Item1].Where(row => (row.Date?.Year ?? row.Year) == year &&
                (imdb == null || row.Imdb == null || string.Equals(row.Imdb, imdb, StringComparison.OrdinalIgnoreCase)))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static string Field(XElement row, string name)
        {
            var value = row.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        internal sealed class Record
        {
            internal bool Ambiguous;
            internal string Title, Imdb, Overview, Poster, OfficialRating;
            internal int? Year;
            internal DateTimeOffset? Date;
            internal float? Rating;
        }
    }
}
