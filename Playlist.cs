using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyVideoOnDemand
{
    internal static class Playlist
    {
        private const int MaxBytes = 80 * 1024 * 1024;
        private static readonly HttpClient Http = new HttpClient();
        private static readonly Regex Metadata = new Regex(
            @"^(?<attributes>(?:[^,""]|""[^""]*"")*),(?<title>.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Attribute = new Regex(
            @"(?:^|\s)(?<key>tvg-id|tvg-type|tvg-logo)\s*=\s*(?:""(?<value>[^""]*)""|(?<value>[^\s,]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex YearInParentheses = new Regex(@"\((?<year>(?:18|19|20)\d{2})\)");
        private static readonly Regex ReleaseTags = new Regex(
            @"\[[^\]]*\b(?:\d{3,4}p|web|webrip|web-dl|bluray|hevc|x26[45]|aac|hdtv|nf|amzn|hdr|dv|remux|dts)\b[^\]]*\]|\b\d{3,4}p\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal sealed class Entry
        {
            internal string Title { get; set; }
            internal string Url { get; set; }
            internal string ProviderId { get; set; }
            internal string MediaKind { get; set; }
            internal string Artwork { get; set; }
        }

        internal static async Task<string> ReadAsync(string url, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.UserAgent.ParseAdd("EmbyVideoOnDemand/1.0");
                using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxBytes)
                    {
                        throw new InvalidDataException("The playlist exceeds 80 MB.");
                    }
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[65536];
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false)) != 0)
                        {
                            if (output.Length + read > MaxBytes)
                            {
                                throw new InvalidDataException("The playlist exceeds 80 MB.");
                            }
                            output.Write(buffer, 0, read);
                        }
                        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
                    }
                }
            }
        }

        internal static IEnumerable<Entry> Parse(string text)
        {
            using (var reader = new StringReader(text))
            {
                Entry pending = null;
                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    var line = raw.Trim().TrimStart('\ufeff');
                    if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        pending = null;
                        var meta = Metadata.Match(line.Substring(8));
                        if (!meta.Success)
                        {
                            continue;
                        }
                        pending = new Entry { Title = meta.Groups["title"].Value.Trim() };
                        foreach (Match attr in Attribute.Matches(meta.Groups["attributes"].Value))
                        {
                            var value = attr.Groups["value"].Value.Trim();
                            switch (attr.Groups["key"].Value.ToLowerInvariant())
                            {
                                case "tvg-id": pending.ProviderId = value; break;
                                case "tvg-type": pending.MediaKind = value; break;
                                case "tvg-logo": pending.Artwork = value; break;
                            }
                        }
                    }
                    else if (pending != null && line.Length > 0 && line[0] != '#')
                    {
                        pending.Url = line;
                        yield return pending;
                        pending = null;
                    }
                }
            }
        }

        internal static string DisplayTitle(string value) => ReleaseTags.Replace(value, "").Trim().TrimEnd('-', '.');

        internal static Tuple<string, int?> NormalizeTitle(string value)
        {
            var title = DisplayTitle(value);
            var yearMatch = YearInParentheses.Match(title);
            int? year = null;
            if (yearMatch.Success && string.IsNullOrWhiteSpace(title.Substring(yearMatch.Index + yearMatch.Length)))
            {
                year = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
                title = title.Substring(0, yearMatch.Index);
            }

            var normalized = new StringBuilder(title.Length);
            foreach (var character in title.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    normalized.Append(character == '&' ? " and " : char.IsLetterOrDigit(character)
                        ? char.ToLowerInvariant(character).ToString() : " ");
                }
            }
            return Tuple.Create(Regex.Replace(normalized.ToString().Trim(), @"\s+", " "), year);
        }
    }
}
