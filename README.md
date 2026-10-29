<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-light.svg">
    <img src="assets/logo.svg" alt="Osoko" width="100" height="100">
  </picture>
</p>

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/wordmark-light.svg">
    <img src="assets/wordmark.svg" alt="Osoko" width="240">
  </picture>
</p>

<p align="center"><strong>A headless floating shelf for Windows screenshots.</strong></p>

<p align="center">
  <a href="#download-and-get-started">Download</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#shortcuts">Shortcuts</a> ·
  <a href="#support">Support</a> ·
  <a href="#on-device-and-private">Privacy</a> ·
  <a href="https://github.com/lostmusician/osoko-win/issues">Issues</a> ·
  <a href="#license">License</a>
</p>

<p align="center">
  <a href="https://ko-fi.com/chivan">
    <img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="Support on Ko-fi" height="30">
  </a>
</p>

By default, Windows saves captures to your clipboard or your Screenshots folder, but managing, extracting, or dragging them into other apps often interrupts your flow. So I decided to build my own...

Osoko stores your screenshots in a lightweight stack where you can drag them anywhere, copy them, extract text with local OCR, and let them collapse away for a clean look.

## How it works

1. **Capture.** Press `Win+Shift+S` or `Print Screen` as you normally would. Osoko catches your capture into a floating stack instead of cluttering your clipboard or folder.
2. **Drag or paste.** Drag cards directly into any app. Osoko cleans up the temporary file automatically once dropped. You can also hover and press `1` to copy a 1x downscaled image so HiDPI captures don't blow out chat widths.
3. **Extract text and links.** Native Windows OCR runs on-device in milliseconds. Hover over a card and press `T` to copy recognized text. If an item contains a QR code or barcode, a quick-action pill lets you open or copy the URL with `L`.
4. **Redact sensitive information.** Press `R` to automatically detect and black out API keys, tokens, emails, and credentials with solid bars before sharing.
5. **Pin or share.** Press `P` on any card to pin it so it stays visible while you reference it, or share directly through the card action menu.
6. **Fade away.** If you don't interact with the stack, it auto-collapses off-screen. Move your cursor to the edge grab handle or take another screenshot to bring it back.

**Hover shortcuts.** Osoko runs as a lightweight background app with a system tray icon and ghost shelf window (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`) that never steals focus or appears in screenshots. You can trigger every common action simply by hovering over a card and pressing a single key—no clicking required.

## Shortcuts

### Global Capture Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Win + Shift + S` | Snipping Tool region selection capture |
| `Print Screen` | Full screen capture (when Snipping Tool is set to clipboard) |
| `Alt + Shift + S` | Osoko snippet capture hotkey (optional) |

### Hover Shortcuts

Hover your cursor over any card in the stack to trigger instant actions:

| Key | Action |
| :---: | :--- |
| **`1`** | Copy 1x downscaled (halves HiDPI dimensions for Slack and GitHub) |
| **`2`** or **`C`** | Copy original resolution (full bitmap) |
| **`T`** | Copy recognized text (OCR) |
| **`R`** | Redact sensitive data (API keys, tokens, emails) |
| **`L`** | Open or copy detected QR code link |
| **`P`** | Toggle pin (keeps the card on screen) |
| **`S`** | Save to disk (`Shift+S` for Save As) |
| **`Space`** | Open in default image viewer |
| **`Delete`** or **`Backspace`** | Dismiss card from stack |

## Download and get started

### Option 1: Installer (Recommended)

Download **`OsokoSetup-<version>-x64.exe`** from [GitHub Releases](https://github.com/lostmusician/osoko-win/releases), run the setup wizard, and Osoko will launch in your system tray.

The installer creates a Start Menu entry, optional desktop shortcut, clean uninstaller, and can optionally register Osoko to launch at Windows startup.

### Option 2: Portable Executable

Download **`Osoko.exe`** from [GitHub Releases](https://github.com/lostmusician/osoko-win/releases). It is completely self-contained and runs from any folder without installation.

### Requirements

- **Windows 10 version 2004 (build 19041) or newer**, or **Windows 11** (x64).
- Built-in Windows OCR language pack (installed by default for display languages).
- No administrator rights required: Osoko runs and installs entirely per-user.

## On-device and private

- **100% local.** All text recognition (OCR), QR decoding, image downscaling, and privacy redaction run entirely on your PC using native Windows APIs and local processing.
- **Zero telemetry.** No analytics, no tracking, and zero network calls.
- **Automatic cleanup.** Captures exist in a temporary scratch cache and are cleaned up when dropped, dismissed, or restarted.
- **Capture exclusion.** The shelf uses `WDA_EXCLUDEFROMCAPTURE` so it never captures itself or leaks into screen recordings.

## Support

Osoko is 100% open source under the MIT License and free to use.

If you find Osoko useful and want to support ongoing development, you can [support on Ko-fi](https://ko-fi.com/chivan).

## Development

```powershell
dotnet restore Osoko.sln
dotnet build Osoko.sln -c Release
dotnet test tests\Osoko.Tests\Osoko.Tests.csproj -c Release
```

To build a self-contained single-file executable:

```powershell
dotnet publish src\Osoko\Osoko.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x64
```

Questions, bug reports, and suggestions: [GitHub Issues](https://github.com/lostmusician/osoko-win/issues).

## License

[MIT](LICENSE)
