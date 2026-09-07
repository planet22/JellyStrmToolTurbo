using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.StrmToolTurbo
{
    public class LocalizationManager
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, Dictionary<string, string>> _translations = new Dictionary<string, Dictionary<string, string>>();
        private readonly string _defaultCulture = "en";
        private readonly ILocalizationManager _localizationManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly string _currentCulture;

        // Regex matching the culture code in a resource name (format: Namespace.Folder.culture-code.json)
        // Matches things like "Jellyfin.Plugin.StrmToolTurbo.Resources.zh-CN.json" or "...Resources.en.json"
        private static readonly Regex CultureCodeRegex = new Regex(
            @"^.*\.Resources\.([a-zA-Z]{2}(?:-[a-zA-Z]{2})?)\.json$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public LocalizationManager(ILogger logger, ILocalizationManager localizationManager, IApplicationPaths applicationPaths = null)
        {
            _logger = logger;
            _localizationManager = localizationManager;
            _applicationPaths = applicationPaths;

            LoadAllTranslations();

            // Read the config once at startup and keep it fixed (changes take effect on restart)
            _currentCulture = DetectSystemCulture();
            _logger.LogInformation("Initial culture detected as: {Culture}", _currentCulture);
        }

        /// <summary>
        /// Gets the current culture code (detected at startup, changes take effect on restart).
        /// </summary>
        private string GetCurrentCulture()
        {
            return _currentCulture;
        }

        /// <summary>
        /// Detects the system culture code (from config or the system environment).
        /// </summary>
        private string DetectSystemCulture()
        {
            // First try reading UICulture from the Jellyfin config file
            var configCulture = GetCultureFromJellyfinConfig();
            if (!string.IsNullOrEmpty(configCulture) && _translations.ContainsKey(configCulture))
            {
                return configCulture;
            }

            try
            {
                // Try getting it from the system UI culture
                var systemCulture = CultureInfo.CurrentUICulture ?? CultureInfo.InstalledUICulture;
                var twoLetterLang = systemCulture.TwoLetterISOLanguageName;

                // Prefer Chinese first
                if (twoLetterLang == "zh")
                {
                    if (_translations.ContainsKey("zh-CN"))
                    {
                        return "zh-CN";
                    }
                }

                // Check for an exact culture code match
                if (_translations.ContainsKey(twoLetterLang))
                {
                    return twoLetterLang;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to detect system language");
            }

            // Fall back to the default language
            return _defaultCulture;
        }

        /// <summary>
        /// Reads UICulture from the Jellyfin config file (called once at startup).
        /// </summary>
        private string GetCultureFromJellyfinConfig()
        {
            try
            {
                if (_applicationPaths == null)
                {
                    return null;
                }

                var configPath = Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "system.xml");
                if (!File.Exists(configPath))
                {
                    _logger.LogInformation("Jellyfin config file not found at {Path}", configPath);
                    return null;
                }

                var doc = XDocument.Load(configPath);
                var uiCultureElement = doc.Root?.Element("UICulture");
                if (uiCultureElement != null && !string.IsNullOrEmpty(uiCultureElement.Value))
                {
                    var culture = uiCultureElement.Value.Trim();
                    _logger.LogDebug("Loaded UICulture from config: {Culture}", culture);
                    return culture;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read Jellyfin config file");
            }

            return null;
        }

        private void LoadAllTranslations()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            _logger.LogDebug("Found {Count} embedded resources", resourceNames.Length);
            foreach (var name in resourceNames)
            {
                _logger.LogDebug("Resource: {Name}", name);
            }

            foreach (var resourceName in resourceNames)
            {
                if (resourceName.EndsWith(".json"))
                {
                    try
                    {
                        // Use a regex to parse the culture code (format: *.Resources.zh-CN.json)
                        var match = CultureCodeRegex.Match(resourceName);
                        if (!match.Success)
                        {
                            _logger.LogWarning("Invalid resource name format: {Name}, expected pattern: *.Resources.culture-code.json", resourceName);
                            continue;
                        }
                        var cultureName = match.Groups[1].Value;

                        using var stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            var content = reader.ReadToEnd();
                            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(content);

                            if (translations != null)
                            {
                                _translations[cultureName] = translations;
                                _logger.LogDebug("Loaded {Count} translations for culture {Culture}",
                                    translations.Count, cultureName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load translation file: {Name}", resourceName);
                    }
                }
            }

            _logger.LogInformation("Loaded translations for {Count} cultures: {Cultures}",
                _translations.Count, string.Join(", ", _translations.Keys));

            if (!_translations.ContainsKey(_defaultCulture))
            {
                _logger.LogWarning("Default culture '{Culture}' translations not found", _defaultCulture);
            }
        }

        public string GetLocalizedString(string key, params object[] args)
        {
            return GetLocalizedString(key, _currentCulture, args);
        }

        public string GetLocalizedString(string key, string culture = null, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(key))
                return key;

            var actualCulture = string.IsNullOrWhiteSpace(culture) ? GetCurrentCulture() : culture;

            // Try using the specified culture
            if (_translations.TryGetValue(actualCulture, out var cultureTranslations) &&
                cultureTranslations.TryGetValue(key, out var translation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(translation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to format translated string '{Translation}' with args {Args}",
                            translation, string.Join(", ", args));
                    }
                }
                return translation;
            }

            // Try matching by language prefix (e.g. zh-CN matching zh)
            var languageCode = actualCulture.Split('-', '_')[0];
            if (languageCode != actualCulture &&
                _translations.TryGetValue(languageCode, out var languageTranslations) &&
                languageTranslations.TryGetValue(key, out var languageTranslation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(languageTranslation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to format translated string '{Translation}' with args {Args}",
                            languageTranslation, string.Join(", ", args));
                    }
                }
                return languageTranslation;
            }

            // Fall back to the default culture
            if (_translations.TryGetValue(_defaultCulture, out var defaultTranslations) &&
                defaultTranslations.TryGetValue(key, out var defaultTranslation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(defaultTranslation, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to format translated string '{Translation}' with args {Args}",
                            defaultTranslation, string.Join(", ", args));
                    }
                }
                return defaultTranslation;
            }

            // No translation found, return the original key
            _logger.LogDebug("Translation not found for key '{Key}' in culture '{Culture}'", key, actualCulture);
            return key;
        }
    }
}
