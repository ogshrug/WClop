<div align="center">

<img src="docs/logo.png" alt="WClop logo" width="128" height="128">

# WClop

**Automatic image, video, PDF and audio optimiser for Windows.**

Copy a screenshot, drop a file on the edge of the screen, save a screen recording:<br>
WClop quietly makes it smaller and puts it where you need it, with the original always one click away.

[![Latest release](https://img.shields.io/github/v/release/ogshrug/WClop?label=release)](https://github.com/ogshrug/WClop/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/ogshrug/WClop/total)](https://github.com/ogshrug/WClop/releases)
[![CI](https://github.com/ogshrug/WClop/actions/workflows/ci.yml/badge.svg)](https://github.com/ogshrug/WClop/actions/workflows/ci.yml)
[![Licence: GPL-3.0](https://img.shields.io/badge/licence-GPL--3.0-blue)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)

<a href="https://github.com/ogshrug/WClop/releases/latest/download/WClop-x64.msi"><img src="https://img.shields.io/badge/Download%20for%20Windows-x64-195BBC?style=for-the-badge" alt="Download for Windows (x64)" height="40"></a>
&nbsp;
<a href="https://github.com/ogshrug/WClop/releases/latest/download/WClop-arm64.msi"><img src="https://img.shields.io/badge/Windows%20on%20Arm-arm64-555555?style=for-the-badge" alt="Download for Windows on Arm" height="40"></a>

[Features](#features) · [Install](#install) · [Getting started](#getting-started) · [All features in detail](docs/FEATURES.md) · [Contributing](CONTRIBUTING.md)

</div>

---

WClop is a free, open-source Windows take on [Clop](https://github.com/FuzzyIdeas/Clop), the macOS clipboard optimiser. It runs in the tray, works entirely on your PC, and uses proven tools (pngquant, jpegoptim, gifsicle, FFmpeg, Ghostscript) to shrink files, often to a fraction of their size, without visible loss.

## Features

### Works on its own

- **Clipboard:** copy an image from the Snipping Tool, a browser or anything else, and WClop swaps it for an optimised version, ready to paste. Turn on *Collect results* to keep every optimised copy and paste them all at once.
- **Watched folders:** new screenshots and screen recordings are optimised as they're saved (Pictures\Screenshots and Videos\Captures by default; add any folder).

### When you want it

- **Drop zone:** drag files towards the edge of the screen and a tab slides out. Drop to optimise, or scroll while holding the files to pick a preset: aggressive, half size, *under 10 MB* for Discord, your own pipelines. Tap Alt mid-drag to open it right under the mouse.
- **Explorer:** right-click → *Optimise with WClop*, *Send to → WClop* or *Open with → WClop*. Folders and big selections open batch mode.
- **Hotkeys:** Ctrl+Alt+Shift + Z optimises whatever is on the clipboard, U restores the original, − and 1–9 downscale, A goes aggressive.
- **Batch mode:** whole folders in a progress table, every original backed up first, with a one-click *Restore all*.

### Result cards

Every result shows up as a small card with the before and after size. From the card you can:

- convert in one click from the **format bar** (codecs named, like `mp4 · HEVC`);
- **downscale** or change the **compression** with a slider;
- **crop** to a size or aspect ratio, with smart crop;
- **fit under a size** (say, 10 MB);
- **restore**, **rename**, **Edit with…**, **share**, or run a **pipeline**.

Lots of results collapse into a compact list you can multi-select and drag out together.

### Automation

- **Pipelines:** a small automation language, run from cards, the drop zone, watched folders, the clipboard or the command line:

  ```
  if(regex: "^screenshot") -> downscale(longEdge: 1920) -> convert(webp) -> move(to: "~/Pictures/Web/")
  ```

  Steps cover resizing, converting, cropping, watermarking, speed changes, loudness, renaming and moving, scripts and more.
- **Command line:** `wclop optimise`, `wclop crop`, `wclop pipeline run`, `wclop settings set …`, talking to the running app.
- **AI assistants:** `wclop mcp` is an [MCP](https://modelcontextprotocol.io) server, so Claude and other assistants can optimise files, write pipelines and change settings for you. Scripts stay off limits to them unless you allow it.

### Safe by design

- Originals are backed up before anything is replaced.
- Output is never allowed to be larger than the input.
- Files WClop has already optimised are marked, so they're never processed twice.
- No telemetry and no accounts. WClop only goes online to check for updates and to download an image link you asked it to optimise.

## Supported formats

| Kind | Formats |
| --- | --- |
| Images | PNG, JPEG, GIF (animated too), WebP (animated too), AVIF, HEIC, BMP, TIFF |
| Video | MP4, MOV, WebM, MKV, AVI, MPEG |
| PDF | PDF (images recompressed with adaptive DPI; text, fonts and links kept) |
| Audio | MP3, M4A/AAC, WAV, FLAC, OGG/Opus, AIFF |

## Install

1. Download the installer from the [latest release](https://github.com/ogshrug/WClop/releases/latest):
   - **`WClop-<version>-x64.msi`** for most PCs (Windows 10 or 11).
   - **`WClop-<version>-arm64.msi`** for Windows 11 on Arm (Snapdragon and similar). WClop and FFmpeg run natively; the image and PDF tools have no Arm builds and run under Windows 11's x64 emulation.
2. Run it. It installs for your user only (no administrator rights), adds the Explorer menu, Send To, Open with, a Start menu entry and the `wclop` command, then starts WClop in the tray.

Nothing else needs installing: .NET and all the tools are included.

> [!NOTE]
> The installer isn't code-signed yet, so SmartScreen may show *Windows protected your PC*. Choose **More info → Run anyway**. Each release lists the SHA-256 of its installers if you'd like to check the download.

WClop keeps itself up to date from *Settings → General → Updates*, or automatically if you turn that on. To uninstall, use *Settings → Apps*; it removes everything it added. Your settings stay in `%APPDATA%\WClop` in case you come back.

## Getting started

1. **Take a screenshot** with Win+Shift+S and paste it into a chat. It's already optimised; the card in the corner shows how much smaller it got.
2. **Drag a file** towards the right edge of the screen and drop it on the tab that appears.
3. **Hover a card** and press Ctrl+Alt+Shift+U to restore the original, or Ctrl+Alt+Shift+5 to scale it to 50%.
4. **Open Settings** from the tray icon (or press Ctrl+F there to search) to choose which folders are watched, how hard to compress, and where files go.

[docs/FEATURES.md](docs/FEATURES.md) explains every feature and setting in detail.

## FAQ

<details>
<summary><b>Is this the official Clop for Windows?</b></summary>

No. WClop is an independent project that reimplements Clop's behaviour for Windows, following its design and compression model closely, with thanks to its author. Clop's source is GPL-3.0, and so is WClop's.
</details>

<details>
<summary><b>Will it ruin my files?</b></summary>

Every original is backed up before it's replaced, and *Restore* (the card button, Ctrl+Alt+Shift+U, or *Restore all* in batch mode) puts it back byte for byte. Backups older than 3 days are cleaned up by default; batch backups are kept until you delete them.
</details>

<details>
<summary><b>Why did pasting into Word or Photoshop not change anything?</b></summary>

On purpose. When an image comes with rich content (RTF, OLE objects, SVG, layers), WClop leaves it alone so pasting back into those apps keeps working. Password managers and other apps you add to *ignored apps* are skipped too.
</details>

<details>
<summary><b>Does it upload my files anywhere?</b></summary>

No. Everything runs locally with bundled tools. The only network requests are the update check (to GitHub, carrying nothing about you) and downloading an image link you asked WClop to optimise.
</details>

<details>
<summary><b>How do I report a bug?</b></summary>

[Open an issue](https://github.com/ogshrug/WClop/issues/new/choose) and attach `%LOCALAPPDATA%\WClop\logs\wclop.log` if you can (tray menu → *Open log*).
</details>

## Contributing

Bug reports, ideas and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers building from source, the project layout, and what makes a pull request easy to merge. Please follow the [code of conduct](CODE_OF_CONDUCT.md), and report security issues privately as described in [SECURITY.md](SECURITY.md).

### Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell 7.

```powershell
git clone https://github.com/ogshrug/WClop.git
cd WClop
./scripts/fetch-tools.ps1                     # downloads the bundled tools into tools/
dotnet build WClop.sln
dotnet test WClop.Tests/WClop.Tests.csproj
dotnet run --project WClop                    # the app (tray icon)
./scripts/package.ps1                         # the installer, into artifacts/
```

Releases are built by GitHub Actions when a `v*` tag is pushed; [docs/RELEASING.md](docs/RELEASING.md) has the details.

## Acknowledgements

- **[Clop](https://github.com/FuzzyIdeas/Clop)** by Alin Panaitiu: WClop's design, behaviour and compression model follow Clop closely. Thank you for building it in the open.
- **[Ghostscript](https://www.ghostscript.com/)** by Artifex Software, which does all of WClop's PDF work.
- The other tools WClop drives: [pngquant](https://pngquant.org/), [jpegoptim](https://github.com/tjko/jpegoptim), [gifsicle](https://www.lcdf.org/gifsicle/), [ExifTool](https://exiftool.org/) and [FFmpeg](https://ffmpeg.org/).
- App icon based on [Clipboard](https://icons8.com/icon/Gr1XbweybhTw/clipboard) by [Icons8](https://icons8.com).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for their licences.

## Licence

WClop is free software: you can redistribute it and modify it under the terms of the [GNU General Public License v3.0](LICENSE) or (at your option) any later version.
