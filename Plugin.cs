using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }
        private static readonly Guid _id = new Guid("6107fc8c-883a-4171-b70e-7590658706B9");

        private readonly ILocalizationManager _localizationManager;
        private readonly LocalizationManager _customLocalization;
        private readonly ILogger<Plugin> _logger;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILocalizationManager localizationManager,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _localizationManager = localizationManager;
            _logger = logger;
            _customLocalization = new LocalizationManager(logger, localizationManager, applicationPaths);

            // Log the plugin configuration
            LogConfiguration();

            // Assign the singleton instance only after full initialization
            Instance = this;
        }

        private void LogConfiguration()
        {
            try
            {
                var config = Configuration;
                _logger.LogInformation("Plugin configuration:");
                _logger.LogInformation("  EnableAutoExtract: {Value}", config.EnableAutoExtract);
                _logger.LogInformation("  EnableMediaInfoCache: {Value}", config.EnableMediaInfoCache);
                _logger.LogInformation("  RefreshDelayMs: {Value}", config.RefreshDelayMs);
                _logger.LogInformation("  MaxConcurrentExtract: {Value}", config.MaxConcurrentExtract);
                _logger.LogInformation("  ForceRefreshIgnoreExisting: {Value}", config.ForceRefreshIgnoreExisting);
                _logger.LogInformation("  ForceRefreshIgnoreCache: {Value}", config.ForceRefreshIgnoreCache);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log configuration");
            }
        }

        public override string Description
        {
            get
            {
                return _customLocalization.GetLocalizedString("StrmTool.PluginDescription");
            }
        }

        public override string Name
        {
            get { return "StrmToolTurbo"; }
        }

        public override Guid Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets the plugin's configuration page info.
        /// </summary>
        /// <returns>A collection containing the configuration page.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = "Jellyfin.Plugin.StrmToolTurbo.Configuration.configPage.html"
                }
            };
        }

        /// <summary>
        /// Gets a localized string.
        /// </summary>
        /// <param name="key">The string key.</param>
        /// <param name="args">Format arguments.</param>
        /// <returns>The localized string.</returns>
        public string GetLocalizedString(string key, params object[] args)
        {
            try
            {
                // First try the custom resources
                var translation = _customLocalization.GetLocalizedString(key);

                if (args.Length > 0 && !string.IsNullOrEmpty(translation) && translation != key)
                {
                    try
                    {
                        return string.Format(translation, args);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogError(ex, "Invalid format string for key: {Key}", key);
                        return translation;  // Return the unformatted translation
                    }
                }

                return translation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get localized string: {Key}", key);
                return key;
            }
        }
    }
}
