using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class ExtractTask : IScheduledTask, IDisposable
    {
        protected volatile bool _disposed = false;
        protected readonly ILogger _logger;
        protected readonly StrmMediaInfoService _mediaInfoService;
        protected readonly MediaInfoCache _mediaCache;
        protected volatile PluginConfiguration _config;
        protected readonly SemaphoreSlim _semaphore;

        private readonly ILibraryManager _libraryManager;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private LibraryScanListener _scanListener;
        private ItemUpdateListener _updateListener;
        private readonly CancellationTokenSource _backgroundTaskCts = new CancellationTokenSource();
        private readonly object _eventLock = new object();

        public ExtractTask(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger<ExtractTask> logger)
        {
            _logger = logger;
            _mediaInfoService = new StrmMediaInfoService(libraryManager, mediaEncoder, mediaStreamRepository, itemRepository, logger);
            _mediaCache = new MediaInfoCache(logger);

            if (Plugin.Instance == null)
            {
                _logger.LogWarning("Plugin instance not found, using default configuration");
                _config = new PluginConfiguration();
            }
            else
            {
                _config = Plugin.Instance.Configuration;
            }

            _semaphore = new SemaphoreSlim(_config.MaxConcurrentExtract);

            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;

            try
            {
                _scanListener = new LibraryScanListener(_libraryManager, _logger, _config);
                _scanListener.StrmFileDetected += OnStrmFileDetected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize library scan listener");
            }

            try
            {
                _updateListener = new ItemUpdateListener(_libraryManager, _logger, _config, _mediaCache);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize item update listener");
            }
        }

        public string Category => "StrmToolTurbo";
        public string Key => "StrmToolTask";
        public string Description => Plugin.Instance?.GetLocalizedString("StrmTool.TaskDescription") ?? "Extract media technical information (codec, resolution, subtitles) from strm files";
        public string Name => Plugin.Instance?.GetLocalizedString("StrmTool.TaskName") ?? "Extract Strm Media Info";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        protected void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                // Note: MaxConcurrentExtract only takes effect after a Jellyfin restart,
                // to avoid replacing the semaphore at runtime and breaking concurrency control
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// Generic helper for processing a batch of strm file items.
        /// </summary>
        protected async Task<int> ProcessStrmItemsAsync(
            List<BaseItem> items,
            Func<BaseItem, CancellationToken, Task> processItemAsync,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            int processed = 0;
            int total = items.Count;

            var tasks = items.Select(async item =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await processItemAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing {Name} ({Path})", item.Name, item.Path);
                }
                finally
                {
                    _semaphore.Release();
                    int current = Interlocked.Increment(ref processed);
                    double percent = Math.Min((double)current / total * 100, 100);
                    progress.Report(percent);
                }
            });

            await Task.WhenAll(tasks);
            progress.Report(100);
            return processed;
        }

        /// <summary>
        /// Attempts to load media streams from the cache, saving them directly on success.
        /// When <see cref="PluginConfiguration.ImportExistingCacheWhenMissing"/> is enabled, also
        /// imports the cached Size/RunTimeTicks/Container into Jellyfin's database, since this path
        /// only runs for items whose media info Jellyfin doesn't already have.
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether the cache load succeeded.</returns>
        private async Task<bool> TryLoadFromCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!_config.EnableMediaInfoCache || _config.ForceRefreshIgnoreCache)
            {
                return false;
            }

            if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
            {
                return false;
            }

            try
            {
                var mediaStreams = cacheData.MediaStreams ?? new List<MediaStream>();
                _mediaInfoService.SaveMediaStreams(item.Id, mediaStreams, cancellationToken);
                _logger.LogInformation("{Name}: Used cached media info ({Count} streams)",
                    item.Name, mediaStreams.Count);

                if (_config.ImportExistingCacheWhenMissing)
                {
                    await ImportItemMetadataFromCacheAsync(item, cacheData, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: Failed to save cached media streams", item.Name);
                return false;
            }
        }

        /// <summary>
        /// Imports Size/RunTimeTicks/Container from a cache entry into Jellyfin's database for an
        /// item that's missing its media info, so the item matches what the cache already recorded.
        /// </summary>
        private async Task ImportItemMetadataFromCacheAsync(BaseItem item, MediaInfoCacheData cacheData, CancellationToken cancellationToken)
        {
            if (cacheData.Size <= 0)
            {
                // A Size of 0 means this cache entry predates capturing Size/RunTimeTicks/Container
                // (or the probe never got them); importing it would just clobber good data with zero.
                _logger.LogDebug("{Name}: Cached Size is {Size}, skipping metadata import", item.Name, cacheData.Size);
                return;
            }

            try
            {
                item.Size = cacheData.Size;
                item.RunTimeTicks = cacheData.RunTimeTicks;
                item.Container = cacheData.Container;

                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("{Name}: Imported item metadata from cache (Size={Size})", item.Name, cacheData.Size);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name}: Failed to import item metadata from cache", item.Name);
            }
        }

        /// <summary>
        /// Probes media streams and saves the result to the cache.
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The probe result.</returns>
        private async Task<MediaProbeResult> ProbeAndCacheAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var probeResult = await _mediaInfoService.ProbeMediaStreamsAsync(item, cancellationToken).ConfigureAwait(false);

            if (_config.EnableMediaInfoCache && probeResult.Success)
            {
                await _mediaCache.SaveFullCacheAsync(
                    item.Path,
                    probeResult.MediaStreams,
                    probeResult.Size,
                    probeResult.RunTimeTicks,
                    probeResult.Container,
                    cancellationToken).ConfigureAwait(false);
            }

            return probeResult;
        }

        private void OnStrmFileDetected(object sender, BaseItem item)
        {
            if (_disposed)
                return;

            RefreshConfig();

            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            if (!_config.EnableAutoExtract)
            {
                _logger.LogDebug("Auto-extract is disabled, skipping {Name}", fileName);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var timeoutMinutes = _config?.MetadataRestoreTimeoutMinutes ?? 5;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundTaskCts.Token);
                    cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
                    await Task.Delay(_config.RefreshDelayMs, cts.Token).ConfigureAwait(false);

                    if (_disposed)
                        return;

                    await ExtractSingleItemAsync(item, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Extraction cancelled for {Name}", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in auto-extract background task for {Name}", fileName);
                }
            }, _backgroundTaskCts.Token);
        }

        public void CleanupListener()
        {
            lock (_eventLock)
            {
                if (_scanListener != null)
                {
                    _scanListener.StrmFileDetected -= OnStrmFileDetected;
                    _scanListener.Dispose();
                    _scanListener = null;
                }

                if (_updateListener != null)
                {
                    _updateListener.Dispose();
                    _updateListener = null;
                }
            }
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            RefreshConfig();
            _logger.LogInformation("Starting strm file scan...");

            try
            {
                var allStrmItems = _mediaInfoService.GetAllStrmItems(cancellationToken);
                var strmItems = new List<BaseItem>();
                int totalFound = allStrmItems.Count;

                foreach (var item in allStrmItems)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var mediaStreams = _mediaInfoService.GetItemMediaStreams(item);
                        bool hasVideo = mediaStreams.Any(s => s.Type == MediaStreamType.Video);
                        bool hasAudio = mediaStreams.Any(s => s.Type == MediaStreamType.Audio);

                        if (_config.ForceRefreshIgnoreExisting || !(hasVideo || hasAudio))
                        {
                            strmItems.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking media streams for {Name}", item.Name);
                        strmItems.Add(item);
                    }
                }

                _logger.LogInformation("Found {Count} strm files in library, {NeedRefresh} need media info",
                    totalFound, strmItems.Count);

                if (strmItems.Count == 0)
                {
                    progress.Report(100);
                    _logger.LogInformation("Nothing to process, task complete.");
                    return;
                }

                await ProcessStrmFiles(strmItems, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during strm file scan");
                throw;
            }
        }

        private async Task ProcessStrmFiles(List<BaseItem> strmItems, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int processed = await ProcessStrmItemsAsync(strmItems, ProcessSingleItemAsync, progress, cancellationToken);

            _logger.LogInformation("Task complete. Successfully processed {Processed}/{Total} strm files.",
                processed, strmItems.Count);
        }

        /// <summary>
        /// Core processing logic: load media streams from cache, or probe for them.
        /// </summary>
        private async Task<(List<MediaStream> before, List<MediaStream> after)> ProcessItemCoreAsync(
            BaseItem item, 
            string logPrefix,
            CancellationToken cancellationToken)
        {
            var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
            _logger.LogDebug("{Prefix} - Before: {Count} streams", logPrefix, beforeStreams.Count);

            // First try loading from the cache
            bool loadedFromCache = await TryLoadFromCacheAsync(item, cancellationToken).ConfigureAwait(false);

            if (!loadedFromCache)
            {
                // Cache miss - probe and save the result to the cache
                await ProbeAndCacheAsync(item, cancellationToken).ConfigureAwait(false);
            }

            var afterStreams = _mediaInfoService.GetItemMediaStreams(item);
            return (beforeStreams, afterStreams);
        }

        private async Task ProcessSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing {Name}", item.Name);

            var (beforeStreams, afterStreams) = await ProcessItemCoreAsync(
                item, 
                "StrmToolTurbo",
                cancellationToken).ConfigureAwait(false);

            bool hasVideo = afterStreams.Any(s => s.Type == MediaStreamType.Video);
            bool hasAudio = afterStreams.Any(s => s.Type == MediaStreamType.Audio);

            _logger.LogInformation(
                "{Name}: Probe done. Streams {Before}→{After}. Video:{Video}, Audio:{Audio}",
                item.Name,
                beforeStreams.Count,
                afterStreams.Count,
                hasVideo,
                hasAudio
            );

            if (!(hasVideo || hasAudio))
            {
                _logger.LogWarning("{Name} may still lack media stream info", item.Name);
            }
        }

        public async Task ExtractSingleItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            RefreshConfig();
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("Auto-extracting media info for new strm file: {Name}", fileName);

                var beforeStreams = _mediaInfoService.GetItemMediaStreams(item);
                _logger.LogDebug("Before: {Count} streams", beforeStreams.Count);

                bool hasVideo = beforeStreams.Any(s => s.Type == MediaStreamType.Video);
                bool hasAudio = beforeStreams.Any(s => s.Type == MediaStreamType.Audio);

                if (!_config.ForceRefreshIgnoreExisting && (hasVideo || hasAudio))
                {
                    _logger.LogInformation("{Name} already has media stream info, skipping", fileName);
                    return;
                }

                // Use the shared helper to handle caching and probing
                var (_, afterStreams) = await ProcessItemCoreAsync(
                    item, 
                    "Auto-extract", 
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Auto-extract complete for {Name}. Streams {Before}→{After}",
                    fileName,
                    beforeStreams.Count,
                    afterStreams.Count
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-extract for {Name}", fileName);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing)
            {
                try
                {
                    _backgroundTaskCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                CleanupListener();
                _backgroundTaskCts.Dispose();
                _semaphore.Dispose();
            }
        }
    }
}
