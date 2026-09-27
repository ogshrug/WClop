<div align="center">

<img src="docs/logo.png" alt="WClop logo" width="128" height="128">

# WClop

<a href="https://github.com/ogshrug/WClop/releases/latest/download/WClop-x64.msi"><img src="https://img.shields.io/badge/Download%20for%20Windows-195BBC?style=for-the-badge" alt="Download for Windows" height="40"></a>

</div>

**Automatic image, video, PDF and audio optimiser for Windows.** Copy a screenshot, drop a file on the edge of the screen, save a screen recording: WClop quietly makes it smaller and puts the result where you need it, with the original always one click away.

WClop is a Windows take on [Clop](https://github.com/FuzzyIdeas/Clop) for macOS.

[All releases](https://github.com/ogshrug/WClop/releases) · [Every feature, in detail](docs/FEATURES.md)

## What it does

- **Clipboard:** copy an image (Snipping Tool, a browser, anything) and WClop swaps it for an optimised version on the clipboard, usually a fraction of the size, ready to paste.
- **Drop zone:** drag files towards the edge of your screen and a small tab slides out. Drop on it to optimise; scroll while holding the files to pick a preset (aggressive, half size, "under 10 MB" for Discord, your own pipelines…).
- **Watched folders:** new screenshots and screen recordings are optimised as they're saved (Pictures\Screenshots and Videos\Captures by default; add any folder).
- **Explorer:** right-click → *Optimise with WClop*, or *Send to → WClop*. A folder or lots of files open batch mode.
- **Result cards:** every result shows up as a small card with the before/after size and buttons to restore, downscale, fit under a size, convert, run a pipeline or show the file.
- **Hotkeys:** Ctrl+Alt+Shift + Z optimises whatever is on the clipboard, U restores, − and 1–9 downscale, A goes aggressive, and more.
- **Pipelines:** a small automation language, for example `if(regex: "^screenshot") -> downscale(longEdge: 1920) -> convert(webp) -> move(to: "~/Pictures/Web/")`, run from cards, the drop zone, folders, the clipboard or the command line. Includes watermarking images, videos and GIFs with your logo.
- **Batch mode:** optimise whole folders with a progress table, with every original backed up first and a one-click *Restore all*.
- **Command line:** `wclop optimise`, `wclop pipeline run`, `wclop settings set …`, talking to the running app.

Originals are backed up before anything is replaced, output is never allowed to be larger than the input, and files WClop has already optimised are marked so they're never processed twice.

## Supported formats

| Kind | Formats |
| --- | --- |
| Images | PNG, JPEG, GIF (animated too), WebP, AVIF, HEIC, BMP, TIFF |
| Video | MP4, MOV, WebM, MKV, AVI, MPEG |
| PDF | PDF (image recompression with adaptive DPI) |
| Audio | MP3, M4A/AAC, WAV, FLAC, OGG/Opus, AIFF |

## Install

Download `WClop-<version>-x64.msi` from [Releases](https://github.com/ogshrug/WClop/releases) and run it. It installs for your user only (no administrator rights), adds the Explorer menu, Send To, a Start menu entry and the `wclop` command, and starts WClop. Windows 10 or 11, x64. Nothing else needs installing: .NET and the tools are included.

The installer isn't code-signed yet, so SmartScreen may say *Windows protected your PC*: choose **More info → Run anyway**. Each release lists the installer's SHA-256 checksum.

WClop keeps itself up to date: it checks for new versions and installs them from *Settings → General → Updates* (or automatically, if you turn that on).

Uninstall from *Settings → Apps*; it removes everything it added.

## Build from source

Needs the .NET 10 SDK and PowerShell.

```powershell
./scripts/fetch-tools.ps1          # downloads pngquant, jpegoptim, gifsicle, exiftool, ffmpeg, Ghostscript into tools/
dotnet build WClop.sln
dotnet test WClop.Tests/WClop.Tests.csproj
dotnet run --project WClop         # the app (tray icon)
./scripts/package.ps1              # the installer, into artifacts/
```

Releases are built by GitHub Actions: pushing a tag like `v1.2.3` runs the tests, builds the installer and publishes it.

## Project layout

```
WClop/          The app: tray, clipboard, drop zone, result cards, settings, hotkeys, batch, Explorer integration
WClop.Core/     Engines and logic: optimisation, video/PDF/audio, pipelines, storage, settings, local API
WClop.Cli/      The wclop command
WClop.Tests/    xUnit tests
installer/      WiX 5 installer
scripts/        fetch-tools, package, make-icon
```

## Thanks

- **[Clop](https://github.com/FuzzyIdeas/Clop)** by Alin Panaitiu: WClop's design, behaviour and compression model follow Clop closely. Thank you for building it in the open.
- **[Ghostscript](https://www.ghostscript.com/)** by Artifex Software, which does all of WClop's PDF work.
- The other tools WClop drives: [pngquant](https://pngquant.org/), [jpegoptim](https://github.com/tjko/jpegoptim), [gifsicle](https://www.lcdf.org/gifsicle/), [ExifTool](https://exiftool.org/) and [FFmpeg](https://ffmpeg.org/).
- App icon based on [Clipboard](https://icons8.com/icon/Gr1XbweybhTw/clipboard) by [Icons8](https://icons8.com).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for their licences.

## Licence

WClop is free software under the [GNU General Public License v3.0](LICENSE) (or any later version).
