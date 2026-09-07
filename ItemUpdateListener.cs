using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    /// <summary>
    /// Listens for item update events and restores metadata from cache when a strm file's Size (or other fields) gets reset.
    /// </summary>
    public class ItemUpdateListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly MediaInfoCache _mediaCache;
        private readonly ConcurrentDictionary<Guid, Task> _runningTasks;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
            : this(libraryManager, logger, config, null)
        {
        }

        public ItemUpdateListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config,
            MediaInfoCache mediaCache)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaCache = mediaCache ?? new MediaInfoCache(logger);
            _runningTasks = new ConcurrentDictionary<Guid, Task>();
            _config = config;

            // Subscribe to the item update event
            _libraryManager.ItemUpdated += OnItemUpdated;
            _logger.LogInformation("Item update listener initialized");
        }

        /// <summary>
        /// Refreshes the config reference.
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        /// <summary>
        /// Cleans up completed tasks to prevent a memory leak.
        /// </summary>
        private void CleanupCompletedTasks()
        {
            // Only clean up once the task count exceeds the threshold, to reduce overhead
            if (_runningTasks.Count <= 100)
            {
                return;
            }

            try
            {
                int removed = 0;
                foreach (var kvp in _runningTasks)
                {
                    if (kvp.Value.IsCompleted)
                    {
                        // Observe the task's exception (prevents an unobserved exception)
                        if (kvp.Value.Exception != null)
                        {
                            _logger.LogDebug(kvp.Value.Exception, "Observed completed task exception");
                        }

                        if (_runningTasks.TryRemove(kvp.Key, out _))
                        {
                            removed++;
                        }
                    }
                }

                if (removed > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} completed tasks, remaining: {Remaining}",
                        removed, _runningTasks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up completed tasks");
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                RefreshConfig();

                // Check whether this is a strm file
                if (!StrmMediaInfoService.IsStrmFile(e.Item.Path))
                {
                    return;
                }

                var item = e.Item;
                var fileName = Path.GetFileNameWithoutExtension(item.Path);

                // Check whether a cache entry exists
                if (!_mediaCache.TryGetFullCache(item.Path, out var cacheData))
                {
                    return;
                }

                // Check whether Size was reset (current Size is significantly smaller than the cached Size)
                bool sizeReset = cacheData.Size > 0 && item.Size < cacheData.Size / 10;
                bool needsUpdate = sizeReset;

                if (!needsUpdate)
                {
                    // Check whether other metadata was lost
                    if (cacheData.RunTimeTicks.HasValue && !item.RunTimeTicks.HasValue)
                    {
                        needsUpdate = true;
                    }
                }

                if (!needsUpdate)
                {
                    return;
                }

                // Reserve the slot atomically to avoid a race condition and to double
                // as the task-tracking key, so the same item can't spawn multiple restore tasks.
                var taskKey = item.Id;

                // Check up front whether a restore is already in progress
                if (_runningTasks.ContainsKey(taskKey))
                {
                    _logger.LogDebug("Item {Name} already being restored, skipping", fileName);
                    return;
                }

                // Reserve the slot with a placeholder task before any background work starts,
                // so Dispose() can still await completion of the real work (not just the first await).
                var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                if (!_runningTasks.TryAdd(taskKey, completionSource.Task))
                {
                    _logger.LogDebug("Item {Name} already being restored, skipping", fileName);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var timeoutMinutes = _config?.MetadataRestoreTimeoutMinutes ?? 5;
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));

                        // Re-fetch the latest item, avoiding use of a potentially stale object
                        var latestItem = _libraryManager.GetItemById(item.Id);
                        if (latestItem == null)
                        {
                            _logger.LogWarning("Item {Name} not found in library, skipping restore", fileName);
                            return;
                        }

                        await RestoreItemMetadataAsync(latestItem, cacheData, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("Restore operation cancelled for {Name}", fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error restoring metadata for {Name}", fileName);
                    }
                    finally
                    {
                        _runningTasks.TryRemove(taskKey, out _);
                        completionSource.TrySetResult(true);
                    }
                });

                // Periodically clean up completed tasks to prevent a memory leak
                CleanupCompletedTasks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemUpdated handler");
            }
        }

        private async Task RestoreItemMetadataAsync(BaseItem item, MediaInfoCacheData cacheData, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);

            try
            {
                _logger.LogInformation("Restoring metadata for {Name} (Size: {OldSize} -> {NewSize})",
                    fileName, item.Size, cacheData.Size);

                // Restore metadata (only the fields needed for front-end display and by Jellyfin internally)
                // Note: Jellyfin doesn't reset media stream info, so MediaStreams doesn't need to be restored
                item.Size = cacheData.Size;
                item.RunTimeTicks = cacheData.RunTimeTicks;
                item.Container = cacheData.Container;

                // Persist the changes
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Successfully restored metadata for {Name}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore metadata for {Name}", fileName);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
            catch (ObjectDisposedException)
            {
            }

            // Wait for all background tasks to complete (up to 30 seconds)
            // Use Task.Run to avoid a potential deadlock from blocking synchronously
            try
            {
                var allTasks = _runningTasks.Values.ToArray();
                if (allTasks.Length > 0)
                {
                    Task.Run(async () =>
                    {
                        var waitTask = Task.WhenAll(allTasks);
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                        var completedTask = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);

                        if (completedTask == timeoutTask)
                        {
                            _logger.LogWarning("Timeout waiting for {Count} background tasks to complete", allTasks.Length);
                        }
                    }).Wait(TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for background tasks to complete");
            }

            _logger.LogInformation("Item update listener disposed");
        }
    }
}
