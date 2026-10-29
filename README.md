# Slideshow Web Server

A self-contained .NET 9 web application that displays images and videos in a browser from a local directory or SMB share, mirrored into a local disk cache so network hiccups never stall playback.

- [Features](#features)
- [Configuration](#configuration)
- [Running the Application](#running-the-application)
- [Windows Service Installation](#windows-service-installation)
- [Linux Systemd Installation](#linux-systemd-installation)
- [Security and Service Account Configuration](#security-and-service-account-configuration)

## Features

- **Media File Support**: Images (JPG, JPEG, PNG, GIF, WebP, BMP, SVG) and videos (MP4, WebM, OGG, AVI, MOV).
- **Resilient Local Cache**: Mirrors remote SMB or local source files to a local cache directory using atomic renames, serving all HTTP traffic from local disk.
- **Self-Healing Kiosk UI**: Retries startup fetches with backoff, refreshes the playlist in the background, and skips stalled media automatically.
- **Service Integration**: Native Windows Service (`UseWindowsService`) and Linux systemd (`UseSystemd`, `Type=notify`) support.
- **Single-File Deployment**: Trimmed, self-contained executable with embedded web assets.

## Configuration

The server searches for `config.json` in the following order:

1. Same directory as the executable (`AppContext.BaseDirectory`)
2. Current working directory

Relative paths in `config.json` resolve relative to `AppContext.BaseDirectory`.

```json
{
  "imageDuration": 5,
  "folderPath": "./media",
  "syncIntervalSeconds": 60,
  "fadeTransitionDuration": 1,
  "zoomOnImage": true,
  "displayOrder": "Alpha"
}
```

- `imageDuration`: Seconds to display each image (minimum `1`).
- `folderPath`: Source folder containing media files (local folder or mounted SMB share).
- `cacheFolderPath`: Optional local directory used to cache mirrored media. Defaults to systemd's `CACHE_DIRECTORY` (`/var/cache/slideshow`) when set, or `./cache` beside the executable.
- `syncIntervalSeconds`: Polling interval in seconds for syncing `folderPath` into `cacheFolderPath` (minimum `5`, default `60`).
- `fadeTransitionDuration`: Cross-fade duration in seconds between slides (`0` or greater).
- `zoomOnImage`: Enables a subtle zoom animation on images.
- `displayOrder`: `"Alpha"` for alphabetical order or `"Random"` for shuffled playback.
- `allowEmptySourceSync`: Optional boolean (default `false`). When `false`, an empty source directory does not delete existing cached slides so an unmounted share cannot wipe playback.

### Command Line and Environment Overrides

Configuration precedence runs from lowest to highest: built-in defaults, `config.json`, environment variables, command-line arguments.

By default, the server listens only on `http://localhost:5500`. Pass `--urls` (or set `ASPNETCORE_URLS`) to change ports or listen on LAN interfaces:

```batch
# Listen on localhost with a custom port
SlideshowWebServer.exe --urls "http://localhost:8080"

# Listen on all network interfaces on port 5500
SlideshowWebServer.exe --urls "http://*:5500"

# Listen on a specific IP address
SlideshowWebServer.exe --urls "http://192.168.1.100:5500"

# Override slideshow and logging settings from the command line
SlideshowWebServer.exe --folderPath "D:\Photos" --imageDuration 8 --Logging:LogLevel:Default=Debug
```

## Running the Application

### Logging

Standalone runs log to the console. Windows service runs also log to the Windows Event Log under `Application` (`SlideshowService`).

### Security Notes

- The server binds to `http://localhost:5500` by default so kiosk deployments do not expose media on the local network unless `--urls` or `ASPNETCORE_URLS` opts in.
- Media requests validate filenames against the cache directory, reject symlinks and path separators, set `X-Content-Type-Options: nosniff`, and sandbox SVG responses with a strict `Content-Security-Policy`.
- `/api/config` and `/api/files` return only playback metadata and never expose server filesystem paths.
- Use a reverse proxy such as nginx, Caddy, or IIS to terminate TLS when serving over a network.

### Standalone Application

Run the published executable from `./dist`:

#### Windows (x64)
```batch
SlideshowWebServer.exe
```

#### Linux (x64, ARM64) and macOS (x64, ARM64)
```bash
chmod +x SlideshowWebServer
./SlideshowWebServer
```

### Publishing and Testing from Source

Run the unit and integration test suite:

```bash
dotnet test source/Slideshow.sln
```

Build a self-contained single-file binary into `./dist` using the platform script:

- Windows x64: `buildwin.bat`
- Linux x64: `./buildlinuxx64.sh`
- Linux ARM64: `./buildlinuxarm.sh`
- macOS Intel x64: `./buildmacx64.sh`
- macOS Apple Silicon ARM64: `./buildmacarm.sh`

## Windows Service Installation

Run as Administrator from `dist\win32`:

```batch
install-service.bat
```

The installer creates the `SlideshowService` Windows Event Log source, registers `SlideshowService` (`Slideshow Web Server`) for automatic startup, and starts the service.

To uninstall:

```batch
uninstall-service.bat
```

## Linux Systemd Installation

Linux release archives include `linux/slideshow.service` (`DynamicUser=yes` with `CacheDirectory=slideshow`), `linux/kiosk.service`, `linux/auto.smb`, and `linux/credential.txt`. Full Ubuntu Frame kiosk and CIFS automount setup steps are in `source/linux/ubuntu-kiosk-configuration.md`.

## Security and Service Account Configuration

### Default Windows Service Account

`install-service.bat` configures `SlideshowService` to run as `LocalSystem` for broad local disk compatibility on dedicated kiosk hardware.

Switch to a lower-privilege account (`NT AUTHORITY\NetworkService` or a dedicated local/domain service account) on shared machines or corporate networks:

1. Open `services.msc`, right-click **Slideshow Web Server**, and open **Properties > Log On**.
2. Select **This account** and enter `NT AUTHORITY\NetworkService` or a dedicated account that has read access to `folderPath` and read/write access to `cacheFolderPath`.
3. Use UNC paths (`\\server\share\folder`) instead of per-user mapped drive letters.
4. Restart the service:

```batch
sc stop SlideshowService
sc start SlideshowService
```
