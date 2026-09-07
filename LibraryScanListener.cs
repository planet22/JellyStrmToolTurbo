using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class LibraryScanListener : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private PluginConfiguration _config;
        private volatile bool _isDisposed = false;

        // Uses an event to decouple from ExtractTask rather than depending on it directly
        public event EventHandler<BaseItem> StrmFileDetected;

        public LibraryScanListener(
            ILibraryManager libraryManager,
            ILogger logger,
            PluginConfiguration config)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _config = config;

            // Subscribe to the event
            _libraryManager.ItemAdded += OnItemAdded;
            _logger.LogInformation("Library scan listener initialized");
        }

        /// <summary>
        /// Refreshes the config reference to ensure the latest values are used.
        /// </summary>
        public void RefreshConfig()
        {
            if (Plugin.Instance != null)
            {
                _config = Plugin.Instance.Configuration;
            }
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (_isDisposed)
                return;

            try
            {
                // Refresh config to ensure the latest settings are used
                RefreshConfig();

                if (!_config.EnableAutoExtract)
                    return;

                var item = e.Item;

                // Check whether this is a strm file
                if (!StrmMediaInfoService.IsStrmFile(item.Path))
                {
                    return;
                }

                // Check disposal again to guard against a race condition
                if (_isDisposed)
                    return;

                // Use the actual file name rather than item.Name, since item.Name may not be fully resolved yet
                var fileName = Path.GetFileNameWithoutExtension(item.Path);
                _logger.LogInformation("New strm file detected: {Name} ({Path})", fileName, item.Path);

                // Notify ExtractTask via the event so it can process the newly added file
                StrmFileDetected?.Invoke(this, item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemAdded handler");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _libraryManager.ItemAdded -= OnItemAdded;
            }
            catch (ObjectDisposedException)
            {
            }

            _logger.LogInformation("Library scan listener disposed");
        }
    }
}