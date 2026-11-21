using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyVideoOnDemand
{
    internal sealed class PopularityIndex
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _export;
        private volatile Dictionary<int, double> _scores = new Dictionary<int, double>();

        internal PopularityIndex(string export) { _export = export; }

        internal IReadOnlyDictionary<int, double> Scores => _scores;

        internal void Load(string directory)
        {
            var path = CacheFile(directory);
            if (!File.Exists(path)) return;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    var count = reader.ReadInt32();
                    if (count <= 0 || reader.BaseStream.Length != sizeof(int) + (long)count * (sizeof(int) + sizeof(double)))
                        return;
                    var scores = new Dictionary<int, double>(count);
                    for (var i = 0; i < count; i++)
                        scores[reader.ReadInt32()] = reader.ReadDouble();
                    _scores = scores;
                }
            }
            catch (IOException) { }
        }

        internal async Task Refresh(string directory, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var date = now.Hour < 8 ? now.Date.AddDays(-1) : now.Date;
            var scores = await Download(date, token).ConfigureAwait(false)
                ?? await Download(date.AddDays(-1), token).ConfigureAwait(false);
            if (scores == null || scores.Count == 0)
                throw new IOException("TMDB returned an empty popularity export for " + _export + ".");

            Directory.CreateDirectory(directory);
            var path = CacheFile(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new BinaryWriter(File.Create(temporary)))
                {
                    writer.Write(scores.Count);
                    foreach (var score in scores)
                    {
                        writer.Write(score.Key);
                        writer.Write(score.Value);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                _scores = scores;
            }
            finally { File.Delete(temporary); }
        }

        private string CacheFile(string directory) => Path.Combine(directory, _export + "-popularity-v1.bin");

        private async Task<Dictionary<int, double>> Download(DateTime date, CancellationToken token)
        {
            var url = "https://files.tmdb.org/p/exports/" + _export + "_ids_" +
                date.ToString("MM_dd_yyyy", CultureInfo.InvariantCulture) + ".json.gz";
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    return await ReadExport(response, timeout.Token).ConfigureAwait(false);
                }
            }
        }

        private static async Task<Dictionary<int, double>> ReadExport(HttpResponseMessage response, CancellationToken token)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Forbidden) return null;
            response.EnsureSuccessStatusCode();
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (token.Register(stream.Dispose))
            using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            using (var json = new MemoryStream())
            {
                var scores = new Dictionary<int, double>();
                var serializer = new DataContractJsonSerializer(typeof(ExportTitle));
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0) continue;
                    var bytes = Encoding.UTF8.GetBytes(line);
                    json.SetLength(0);
                    json.Write(bytes, 0, bytes.Length);
                    json.Position = 0;
                    var row = (ExportTitle)serializer.ReadObject(json);
                    if (row.Id > 0 && row.Popularity is double score && score >= 0 && !double.IsInfinity(score))
                        scores[row.Id] = score;
                }
                return scores;
            }
        }

        [DataContract]
        private sealed class ExportTitle
        {
            [DataMember(Name = "id")] public int Id { get; set; }
            [DataMember(Name = "popularity")] public double? Popularity { get; set; }
        }
    }
}
