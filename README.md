This is a fork of https://github.com/jinlin-teck/StrmTool/tree/jellyfin

# StrmToolTurbo for Jellyfin

Jellyfin plugin for extracting media technical information (codec, resolution, subtitles) from strm files to accelerate playback startup speed.

> **Recommended companion**: If you also need to batch-generate strm files from OpenList/Alist, check out my other project: [openlist-strm](https://github.com/jinlin-teck/openlist-strm) — a lightweight service with WebUI that generates .strm files from OpenList/Alist directories. Combined with this plugin, you can play strm media files perfectly on Jellyfin.

🎉 **v2.2.0 Update**: New Size protection mechanism! Automatically restores from cache when metadata like Size of strm files is accidentally reset; also optimizes code structure and error handling.

## Core Features

1. **Early Media Information Extraction**: Immediately requests and obtains media technical information (audio/video codec, resolution, subtitles, etc.) from remote servers after strm files are added to the library
2. **Automatic Extraction for New Files**: Newly added strm files can automatically extract media information in the background when the feature is enabled, no manual intervention required
3. **Media Information Caching**: Automatically caches extracted media information as `.strmtool.json` files with the same name (saved in the same directory as the strm file), allowing direct import during next extraction
4. **Scheduled Task Support**: Provides an `Extract Strm Media Info` scheduled task that supports manual triggering and scheduled execution
5. **Configuration Interface**: Provides a plugin settings page to adjust automatic extraction toggle, refresh delay, persistent cache toggle, maximum concurrency, and force refresh strategies

Compatible with Jellyfin 10.11.0+ (latest 10.11.6 tested, other versions please test yourself)

## Installation

1. Create a new folder `StrmToolTurbo` in Jellyfin's `plugin` directory
2. Place the compiled `Jellyfin.Plugin.StrmToolTurbo.dll` into this folder
3. Restart the Jellyfin service

## Usage

### Plugin Settings

Click the "Settings" button on the plugin details page to adjust the following configuration items:

- **Automatically extract media info for new strm files**: When enabled, newly added strm files will automatically perform media information extraction in the background (Default: Enabled)
- **Enable media info caching**: When enabled, extracted media information will be saved as xxx.strmtool.json files (in the same directory as the strm file) to avoid repeated probing (Default: Enabled)
- **Refresh delay (ms)**: Milliseconds to wait after each media info refresh to avoid overwhelming remote servers (Default: 1000ms)
- **Import existing cache when data missing from Jellyfin**: When enabled, if a cache file exists for a strm file but Jellyfin has no media info for it, the cached size, runtime and container are imported into Jellyfin's database to match the cache (Default: Disabled)
- **Maximum concurrent extractions (restart required)**: Maximum concurrency for media info extraction tasks, range: 1-50 (Default: 5). Requires Jellyfin restart to take effect.
- **Force Refresh Options**:
  - **Ignore existing media streams**: When enabled, will always execute refresh regardless of whether media stream info already exists (cache can still be used)
  - **Ignore cache**: When enabled, will always fetch from remote server directly, ignoring cache files (will still check if media streams exist)

**Note**: Except for "Maximum concurrent extractions", all other configuration changes take effect immediately (automatically applied on next task execution) without restarting Jellyfin.

### Scheduled Tasks

1. Go to Jellyfin admin → Scheduled Tasks
2. Find the `Extract Strm Media Info` task under the `Strm Tool Turbo` category
3. Can be run manually or set to trigger on a schedule
4. This task can use the force refresh options on the settings page to control refresh and cache usage strategies - one task can handle extraction, backup, and recovery functions.

## Notes

- Please select the corresponding plugin version based on your Jellyfin version
- Compared to previous versions, v1.0.0.3 does not call any third-party metadata services, and existing metadata (title, description, posters, etc.) will not be modified
- Media info cache file format is `strm_filename.strmtool.json`, located in the same directory as the strm file
