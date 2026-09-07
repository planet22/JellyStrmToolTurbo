using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        private int _refreshDelayMs = 1000;
        private int _maxConcurrentExtract = 5;

        /// <summary>
        /// Refresh delay time (milliseconds).
        /// </summary>
        public int RefreshDelayMs
        {
            get => _refreshDelayMs;
            set => _refreshDelayMs = Math.Max(0, value);
        }

        /// <summary>
        /// Whether to automatically extract media info for newly added strm files.
        /// </summary>
        public bool EnableAutoExtract { get; set; } = true;

        /// <summary>
        /// Whether to enable the media info cache.
        /// </summary>
        public bool EnableMediaInfoCache { get; set; } = true;

        /// <summary>
        /// Maximum concurrency for extraction tasks (range: 1-50).
        /// </summary>
        public int MaxConcurrentExtract
        {
            get => _maxConcurrentExtract;
            set => _maxConcurrentExtract = Math.Clamp(value, 1, 50);
        }

        /// <summary>
        /// Whether to force a refresh regardless of existing media streams.
        /// </summary>
        public bool ForceRefreshIgnoreExisting { get; set; } = false;

        /// <summary>
        /// Whether to ignore cache files and force fetching from the remote server.
        /// </summary>
        public bool ForceRefreshIgnoreCache { get; set; } = false;

        /// <summary>
        /// Metadata restore timeout (minutes).
        /// </summary>
        public int MetadataRestoreTimeoutMinutes { get; set; } = 5;

        /// <summary>
        /// Whether to import an existing cache file's Size/RunTimeTicks/Container into Jellyfin's
        /// database when that item data is missing, instead of only restoring the media streams.
        /// </summary>
        public bool ImportExistingCacheWhenMissing { get; set; } = false;
    }
}
