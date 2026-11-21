using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;

namespace EmbyVideoOnDemand
{
    public sealed class RefreshPopularityTask : IScheduledTask
    {
        internal static readonly PopularityIndex Movies = new PopularityIndex("movie");
        internal static readonly PopularityIndex Series = new PopularityIndex("tv_series");
        private readonly IApplicationPaths _paths;

        public RefreshPopularityTask(IApplicationPaths paths) { _paths = paths; }

        public string Key => "EmbyVodRefreshPopularity";
        public string Name => "Refresh VoD Popularity";
        public string Description => "Download daily TMDB popularity rankings for VoD movies and TV shows.";
        public string Category => "Video On Demand";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = TimeSpan.FromHours(16).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var directory = CacheDirectory(_paths);
            progress.Report(0);
            var movies = Movies.Refresh(directory, cancellationToken);
            var series = Series.Refresh(directory, cancellationToken);
            await Task.WhenAll(movies, series).ConfigureAwait(false);
            progress.Report(100);
        }

        internal static string CacheDirectory(IApplicationPaths paths) =>
            Path.Combine(paths.PluginConfigurationsPath, "EmbyVideoOnDemand.popularity");
    }

    public sealed class PopularityStartup : IServerEntryPoint
    {
        private readonly ITaskManager _tasks;
        private readonly IApplicationPaths _paths;

        public PopularityStartup(ITaskManager tasks, IApplicationPaths paths)
        {
            _tasks = tasks;
            _paths = paths;
        }

        public void Run()
        {
            var directory = RefreshPopularityTask.CacheDirectory(_paths);
            RefreshPopularityTask.Movies.Load(directory);
            RefreshPopularityTask.Series.Load(directory);
            if (RefreshPopularityTask.Movies.Scores.Count == 0 || RefreshPopularityTask.Series.Scores.Count == 0)
                _tasks.QueueIfNotRunning<RefreshPopularityTask>();
        }

        public void Dispose() { }
    }
}
