using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class MediaInfoCache
    {
        private readonly ILogger _logger;
        private const string CacheFileSuffix = ".strmtool.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public MediaInfoCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the cache file path.
        /// </summary>
        private static string GetCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
                return null;

            // Ensure the path is absolute (Jellyfin's item.Path should always be absolute)
            if (!Path.IsPathRooted(strmPath))
                return null;

            var directory = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var fileName = Path.GetFileNameWithoutExtension(strmPath) + CacheFileSuffix;
            var cachePath = Path.Combine(directory, fileName);

            return cachePath;
        }

        /// <summary>
        /// Checks whether a path contains a path traversal attack pattern.
        /// Used to validate paths found inside strm file content.
        /// </summary>
        public static bool ContainsPathTraversal(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            // Check for a null character (injection attack)
            if (path.Contains('\0'))
            {
                return true;
            }

            // Check for basic path traversal patterns
            var normalized = path.Replace('/', '\\');
            if (normalized.Contains(@"..\") || normalized.Contains(@"\..") ||
                normalized.StartsWith(@"..") || normalized.EndsWith(@".."))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether the cache file exists.
        /// </summary>
        public bool HasCacheFile(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return false;
            }

            var cachePath = GetCachePath(strmPath);
            return !string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath);
        }

        /// <summary>
        /// Validates and returns the cache path (generic validation helper).
        /// </summary>
        private (bool valid, string cachePath) ValidateCachePath(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache lookup");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            if (!File.Exists(cachePath))
            {
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// Validates whether the cache data is valid.
        /// </summary>
        private bool ValidateCacheData(MediaInfoCacheData cache, bool requireMediaStreams = true)
        {
            if (cache?.IsValid != true)
                return false;
            
            if (requireMediaStreams && cache.MediaStreams == null)
                return false;

            return true;
        }

        /// <summary>
        /// Checks for and reads the cache (synchronous version, kept for compatibility).
        /// </summary>
        public bool TryGetCachedMediaStreams(string strmPath, out List<MediaStream> mediaStreams)
        {
            mediaStreams = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache))
                    return false;

                mediaStreams = cache.MediaStreams;
                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// Checks for and reads the cache (asynchronous version).
        /// </summary>
        public async Task<(bool success, List<MediaStream> mediaStreams)> TryGetCachedMediaStreamsAsync(string strmPath, CancellationToken cancellationToken = default)
        {
            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return (false, null);

            try
            {
                var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache))
                    return (false, null);

                _logger.LogDebug("Loaded cached media streams from {Path}", cachePath);
                return (true, cache.MediaStreams);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading cache from {Path}", strmPath);
                return (false, null);
            }
        }

        /// <summary>
        /// Private helper that writes a file atomically.
        /// </summary>
        private async Task WriteFileAtomicallyAsync(string filePath, string content, CancellationToken cancellationToken)
        {
            string tempPath = null;
            try
            {
                tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                CleanupTempFile(tempPath);
                throw;
            }
        }

        /// <summary>
        /// Cleans up the temp file.
        /// </summary>
        private void CleanupTempFile(string tempPath)
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to cleanup temp cache file {Path}", tempPath);
                }
            }
        }

        /// <summary>
        /// Validates the preconditions for saving the cache.
        /// </summary>
        private (bool valid, string cachePath) ValidateSaveCache(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                _logger.LogDebug("Invalid strm path for cache save");
                return (false, null);
            }

            var cachePath = GetCachePath(strmPath);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                return (false, null);
            }

            return (true, cachePath);
        }

        /// <summary>
        /// Saves the cache (atomic write: write a temp file first, then rename).
        /// </summary>
        public async Task SaveCacheAsync(string strmPath, IEnumerable<MediaStream> mediaStreams, CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams.ToList(),
                    IsValid = true
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved cache to {Path}", cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// Saves the full media info cache (includes Size and other metadata).
        /// </summary>
        public async Task SaveFullCacheAsync(
            string strmPath,
            IEnumerable<MediaStream> mediaStreams,
            long size,
            long? runTimeTicks,
            string container,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var (valid, cachePath) = ValidateSaveCache(strmPath);
                if (!valid)
                    return;

                var cache = new MediaInfoCacheData
                {
                    Version = "1.0",
                    Timestamp = DateTime.UtcNow,
                    MediaStreams = mediaStreams?.ToList() ?? new List<MediaStream>(),
                    IsValid = true,
                    Size = size,
                    RunTimeTicks = runTimeTicks,
                    Container = container
                };

                var json = JsonSerializer.Serialize(cache, JsonOptions);
                await WriteFileAtomicallyAsync(cachePath, json, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("Saved full cache (Size={Size}) to {Path}", size, cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving full cache to {Path}", strmPath);
            }
        }

        /// <summary>
        /// Attempts to read the full cache data (includes metadata).
        /// </summary>
        public bool TryGetFullCache(string strmPath, out MediaInfoCacheData cacheData)
        {
            cacheData = null;

            var (valid, cachePath) = ValidateCachePath(strmPath);
            if (!valid)
                return false;

            try
            {
                var json = File.ReadAllText(cachePath);
                var cache = JsonSerializer.Deserialize<MediaInfoCacheData>(json, JsonOptions);

                if (!ValidateCacheData(cache, requireMediaStreams: false))
                    return false;

                cacheData = cache;
                _logger.LogDebug("Loaded full cache (Size={Size}) from {Path}", cache.Size, cachePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading full cache from {Path}", strmPath);
                return false;
            }
        }

        /// <summary>
        /// Clears the cache.
        /// </summary>
        public void ClearCache(string strmPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(strmPath))
                {
                    _logger.LogDebug("Invalid strm path for cache clear");
                    return;
                }

                var cachePath = GetCachePath(strmPath);
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    _logger.LogDebug("Failed to get cache path for: {Path}", strmPath);
                    return;
                }

                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    _logger.LogInformation("Cleared cache: {Path}", cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing cache: {Path}", strmPath);
            }
        }

        /// <summary>
        /// Clears all caches under a directory.
        /// </summary>
        public void ClearAllCaches(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return;

                var jsonFiles = Directory.GetFiles(directoryPath, "*" + CacheFileSuffix, SearchOption.AllDirectories);
                int cleared = 0;

                foreach (var jsonFile in jsonFiles)
                {
                    var fileName = Path.GetFileName(jsonFile);
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(CacheFileSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Derive the strm file name back from the cache file name
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var strmPath = Path.Combine(Path.GetDirectoryName(jsonFile) ?? string.Empty, baseName + StrmMediaInfoService.StrmFileExtension);

                    // Use GetCachePath to verify path safety
                    var expectedCachePath = GetCachePath(strmPath);
                    if (expectedCachePath == null)
                    {
                        continue;
                    }

                    // Make sure the found json file really is the corresponding cache file
                    if (!string.Equals(expectedCachePath, jsonFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Safely delete the cache file
                    try
                    {
                        File.Delete(jsonFile);
                        cleared++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error deleting cache file {Path}", jsonFile);
                    }
                }

                _logger.LogInformation("Cleared {Count} cache files in {Dir}", cleared, directoryPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing caches in {Dir}", directoryPath);
            }
        }
    }

    /// <summary>
    /// Cache data structure.
    /// </summary>
    public class MediaInfoCacheData
    {
        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("mediaStreams")]
        public List<MediaStream> MediaStreams { get; set; }

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("runTimeTicks")]
        public long? RunTimeTicks { get; set; }

        [JsonPropertyName("container")]
        public string Container { get; set; }
    }
}
