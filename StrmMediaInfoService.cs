using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    /// <summary>
    /// Media probe result, including stream info and metadata.
    /// </summary>
    public class MediaProbeResult
    {
        public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
        public long Size { get; set; }
        public long? RunTimeTicks { get; set; }
        public string Container { get; set; }
        public bool Success => MediaStreams != null && MediaStreams.Count > 0;
    }

    /// <summary>
    /// Shared service for STRM media streams: scanning, reading media streams, probing remote media, and writing to the database.
    /// </summary>
    public class StrmMediaInfoService
    {
        // STRM file extension constant
        public const string StrmFileExtension = ".strm";

        /// <summary>
        /// Checks whether a path is a strm file.
        /// </summary>
        public static bool IsStrmFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(StrmFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly IItemRepository _itemRepository;

        public StrmMediaInfoService(
            ILibraryManager libraryManager,
            IMediaEncoder mediaEncoder,
            IMediaStreamRepository mediaStreamRepository,
            IItemRepository itemRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _mediaEncoder = mediaEncoder;
            _mediaStreamRepository = mediaStreamRepository;
            _itemRepository = itemRepository;
            _logger = logger;
        }

        /// <summary>
        /// Recursively finds all strm files under a directory.
        /// </summary>
        /// <param name="directoryPath">The directory path to scan.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The library items for all strm files found.</returns>
        public List<BaseItem> FindStrmFilesInDirectory(string directoryPath, CancellationToken cancellationToken)
        {
            var strmFiles = new List<BaseItem>();
            var dirs = new Stack<string>();
            dirs.Push(directoryPath);

            while (dirs.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var current = dirs.Pop();
                try
                {
                    IEnumerable<string> files = Enumerable.Empty<string>();
                    try
                    {
                        files = Directory.EnumerateFiles(current)
                            .Where(path => IsStrmFile(path));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error enumerating files in {Directory}", current);
                    }

                    foreach (var strmPath in files)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            var item = _libraryManager.FindByPath(strmPath, false);
                            if (item != null)
                            {
                                strmFiles.Add(item);
                            }
                            else
                            {
                                _logger.LogDebug("Could not find library item for path: {Path}", strmPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error processing file {Path}", strmPath);
                        }
                    }

                    IEnumerable<string> subDirs = Enumerable.Empty<string>();
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(current);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error enumerating directories in {Directory}", current);
                    }

                    foreach (var sub in subDirs)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        dirs.Push(sub);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning directory {Directory}", current);
                }
            }

            return strmFiles;
        }

        /// <summary>
        /// Gets all strm files in the library (de-duplicated).
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The library items for all strm files; never null.</returns>
        public List<BaseItem> GetAllStrmItems(CancellationToken cancellationToken)
        {
            var rootFolders = _libraryManager.GetVirtualFolders()
                .SelectMany(vf => vf.Locations)
                .Distinct()
                .ToList();

            var strmItems = new List<BaseItem>();
            foreach (var rootFolder in rootFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    strmItems.AddRange(FindStrmFilesInDirectory(rootFolder, cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder {Folder}", rootFolder);
                }
            }

            return strmItems
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Gets the media stream info for a library item.
        /// </summary>
        /// <param name="item">The library item.</param>
        /// <returns>The list of media streams, or an empty list if unavailable.</returns>
        public List<MediaStream> GetItemMediaStreams(BaseItem item)
        {
            try
            {
                // Use the IHasMediaSources interface to get media streams, avoiding reflection
                if (item is IHasMediaSources hasMediaSources)
                {
                    var streams = hasMediaSources.GetMediaStreams();
                    if (streams != null)
                    {
                        return streams.ToList();
                    }
                }

                return new List<MediaStream>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting media streams for {ItemType}", item.GetType().Name);
                return new List<MediaStream>();
            }
        }

        /// <summary>
        /// Saves the media stream info for a library item.
        /// </summary>
        /// <param name="itemId">The library item ID.</param>
        /// <param name="mediaStreams">The media streams to save.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public void SaveMediaStreams(Guid itemId, List<MediaStream> mediaStreams, CancellationToken cancellationToken)
        {
            _mediaStreamRepository.SaveMediaStreams(itemId, mediaStreams, cancellationToken);
        }

        /// <summary>
        /// Probes media streams and returns the full result (including metadata).
        /// </summary>
        public async Task<MediaProbeResult> ProbeMediaStreamsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var fileName = Path.GetFileNameWithoutExtension(item.Path);
            var result = new MediaProbeResult();

            try
            {
                var strmContent = ReadStrmSourcePath(item.Path);
                if (string.IsNullOrWhiteSpace(strmContent))
                {
                    _logger.LogWarning("STRM file is empty: {Path}", item.Path);
                    return result;
                }

                var isAudio = item.MediaType == MediaType.Audio;
                var mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = new MediaSourceInfo
                        {
                            Path = strmContent,
                            Protocol = GetProtocolFromPath(strmContent),
                        },
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams != null && mediaInfo.MediaStreams.Count > 0)
                {
                    // Save media stream info (not item metadata, to avoid a race with Jellyfin's own metadata reset)
                    // Item metadata gets restored from the cache in ItemUpdateListener
                    _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);

                    // Populate the return result (with the metadata needed for caching)
                    result.MediaStreams = mediaInfo.MediaStreams.ToList();
                    result.Size = mediaInfo.Size.GetValueOrDefault();
                    result.RunTimeTicks = mediaInfo.RunTimeTicks;
                    result.Container = mediaInfo.Container;

                    _logger.LogDebug("Successfully saved {Count} media streams for {Name} (item metadata will be restored later via cache)",
                        mediaInfo.MediaStreams.Count, fileName);
                    return result;
                }

                _logger.LogDebug("No media streams found for {Name}", fileName);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error probing STRM content for {Name}", fileName);
                return result;
            }
        }

        private static MediaProtocol GetProtocolFromPath(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Http;
            }

            if (path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtmp;
            }

            if (path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Rtsp;
            }

            if (path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return MediaProtocol.Ftp;
            }

            return MediaProtocol.File;
        }

        private string ReadStrmSourcePath(string strmFilePath)
        {
            try
            {
                foreach (var line in File.ReadLines(strmFilePath))
                {
                    var sourcePath = line.Trim();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        // Security check: detect path traversal attacks
                        if (MediaInfoCache.ContainsPathTraversal(sourcePath))
                        {
                            _logger.LogWarning("Potential path traversal attack detected in strm file: {Path}", strmFilePath);
                            return string.Empty;
                        }
                        return sourcePath;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "Failed to read strm file: {Path}", strmFilePath);
                return string.Empty;
            }
        }


    }
}
