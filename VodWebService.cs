using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyVideoOnDemand
{
    [Unauthenticated]
    [Route("/Vod/Browse", "GET")]
    public sealed class VodBrowse { }

    [Unauthenticated]
    [Route("/Vod/Assets/{Name}", "GET")]
    public sealed class VodAsset { public string Name { get; set; } }

    [Authenticated(Roles = "Admin")]
    [Route("/Vod/Status", "GET")]
    public sealed class VodStatus { }

    [Authenticated]
    [Route("/Vod/ViewerOptions", "GET")]
    public sealed class VodViewerOptions { }

    [Authenticated]
    [Route("/Vod/Popularity", "GET")]
    public sealed class VodPopularity
    {
        public bool Series { get; set; }
        public int StartIndex { get; set; }
        public int Limit { get; set; } = 20;
        public string SearchTerm { get; set; }
    }

    public sealed class VodWebService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _results;
        private readonly IAuthorizationContext _authorization;
        private readonly ILibraryManager _library;
        private readonly IDtoService _dto;

        public VodWebService(IHttpResultFactory results, IAuthorizationContext authorization, ILibraryManager library, IDtoService dto)
        {
            _results = results;
            _authorization = authorization;
            _library = library;
            _dto = dto;
        }
        public IRequest Request { get; set; }

        public object Get(VodBrowse request) => Resource("index.html", "text/html; charset=utf-8");

        public object Get(VodViewerOptions request) => new
        {
            OpenWatchInNewTab = Plugin.Instance?.CurrentOptions().OpenWatchInNewTab == true
        };

        public object Get(VodStatus request)
        {
            var plugin = Plugin.Instance;
            return plugin?.Channel?.RefreshStatus(plugin.CatalogStampPath)
                ?? new VideoOnDemandChannel.CatalogRefreshStatus("Unavailable");
        }

        public object Get(VodPopularity request)
        {
            var user = _authorization.GetAuthorizationInfo(Request).User;
            var matches = Array.Empty<BaseItem>();
            if (user != null)
            {
                var channel = _library.GetItemList(new InternalItemsQuery(user)
                {
                    Name = "Video On Demand",
                    Recursive = true
                }).OfType<Channel>().FirstOrDefault(item => item.IsVisible(user) &&
                    (user.Policy.EnableAllChannels || (user.Policy.EnabledChannels ?? Array.Empty<string>()).Any(id =>
                        id == item.InternalId.ToString(CultureInfo.InvariantCulture) ||
                        (Guid.TryParse(id, out var channelId) && channelId == item.Id))));
                var shelf = channel == null ? null : _library.GetItemList(new InternalItemsQuery(user)
                {
                    ParentIds = new[] { channel.InternalId },
                    Name = request.Series ? "TV Shows" : "Movies"
                }).OfType<Folder>().FirstOrDefault(item => item.IsVisible(user));
                if (shelf != null)
                {
                    matches = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        ParentIds = new[] { shelf.InternalId },
                        EnforceContentRestriction = true,
                        SearchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm
                    });
                }
            }
            var scores = (request.Series ? RefreshPopularityTask.Series : RefreshPopularityTask.Movies).Scores;
            var ranked = matches.Select(item => (Item: item, Score: Score(item, scores)))
                .OrderByDescending(row => row.Score)
                .ThenBy(row => row.Item.SortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Item.InternalId)
                .ToArray();
            var page = ranked.Skip(Math.Max(0, request.StartIndex)).Take(Math.Min(100, Math.Max(1, request.Limit)))
                .Select(row => row.Item).ToArray();
            return new
            {
                Items = _dto.GetBaseItemDtos(page, new DtoOptions { EnableImages = true, ImageTypeLimit = 1 }, user),
                TotalRecordCount = ranked.Length,
                PopularityReady = scores.Count != 0,
                NoPopularity = ranked.Length != 0 && ranked[0].Score < 0
            };
        }

        private static double Score(BaseItem item, IReadOnlyDictionary<int, double> scores) =>
            item.ProviderIds.TryGetValue("Tmdb", out var value) &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
            scores.TryGetValue(id, out var score) ? score : -1;

        public object Get(VodAsset request)
        {
            switch (request.Name)
            {
                case "catalog.js": return Resource("catalog.js", "application/javascript; charset=utf-8");
                case "catalog.css": return Resource("catalog.css", "text/css; charset=utf-8");
                case "sidebar.js": return Resource("sidebar.js", "application/javascript; charset=utf-8");
                default: throw new FileNotFoundException();
            }
        }

        private object Resource(string name, string mime)
        {
            using (var stream = typeof(VodWebService).Assembly.GetManifestResourceStream("VodWeb." + name))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return _results.GetResult(Request, buffer.ToArray(), mime, new Dictionary<string, string>
                {
                    ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; base-uri 'none'; frame-ancestors 'self'; form-action 'none'",
                    ["Referrer-Policy"] = "no-referrer",
                    ["X-Content-Type-Options"] = "nosniff",
                    ["Cache-Control"] = "no-cache"
                });
            }
        }
    }
}
