This is a fork of https://github.com/jinlin-teck/StrmTool/tree/jellyfin

## Differences from the original

- Retargeted to Jellyfin 12.0 / .NET 10 by default (the original targets Jellyfin 10.11.6 / .NET 9), under its own plugin identity — namespace and assembly name renamed from `StrmTool` to `StrmToolTurbo` so it can be installed alongside the original.
- Added a "Import existing cache when data missing from Jellyfin" option that restores a strm file's Size, RunTimeTicks, and Container from the cache into Jellyfin's database when that data is missing (skips cache entries with a Size of 0 so it never clobbers good data with zero).
- Completely redesigned the plugin settings page as a themed, card-based UI that follows Jellyfin's active theme colors, replacing the original's plain checkbox list.
- The config page's version display now reads the actual running assembly version instead of a hardcoded value, and tolerates different `getInstalledPlugins()` response shapes.
- Fixed a path-traversal sanity check in the media info cache that compared paths against doubled backslashes (`..\\`) and so could never actually match a real path.

# StrmToolTurbo for Jellyfin

Jellyfin plugin for extracting media technical information (codec, resolution, subtitles) from strm files to accelerate playback startup speed.

> **Recommended companion**: If you also need to batch-generate strm files from OpenList/Alist, check out the original author's project: [openlist-strm](https://github.com/jinlin-teck/openlist-strm) — a lightweight service with WebUI that generates .strm files from OpenList/Alist directories. Combined with this plugin, you can play strm media files perfectly on Jellyfin.

## Core Features

1. **Early Media Information Extraction**: Immediately requests and obtains media technical information (audio/video codec, resolution, subtitles, etc.) from remote servers after strm files are added to the library
2. **Automatic Extraction for New Files**: Newly added strm files can automatically extract media information in the background when the feature is enabled, no manual intervention required
3. **Media Information Caching**: Automatically caches extracted media information as `.strmtool.json` files with the same name (saved in the same directory as the strm file), allowing direct import during next extraction
4. **Scheduled Task Support**: Provides an `Extract Strm Media Info` scheduled task that supports manual triggering and scheduled execution
5. **Configuration Interface**: Provides a plugin settings page to adjust automatic extraction toggle, refresh delay, persistent cache toggle, maximum concurrency, and force refresh strategies

Built against Jellyfin 12.0.0 (.NET 10). For Jellyfin 10.11.x servers, use a build targeting net9.0 with Jellyfin.Controller 10.11.6 instead.

## Installation

### Via plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add a new repository with this manifest URL:
   ```
   https://github.com/planet22/JellyStrmToolTurbo/raw/main/manifest.json
   ```
3. Go to **Dashboard → Plugins → Catalog**, find **StrmToolTurbo**, and install it
4. Restart Jellyfin

### Manual install

1. Create a new folder `StrmToolTurbo` in Jellyfin's `plugins` directory
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

- The published release targets Jellyfin 12.0.0 (.NET 10). For Jellyfin 10.11.x servers, build from source targeting `net9.0` with `Jellyfin.Controller` 10.11.6 instead (see the original project's `.csproj` for reference).
- This plugin does not call any third-party metadata services, and existing metadata (title, description, posters, etc.) will not be modified
- Media info cache file format is `strm_filename.strmtool.json`, located in the same directory as the strm file
