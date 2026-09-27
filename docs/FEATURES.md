# WClop features

Everything WClop does, how it behaves, and the settings that change it. Defaults are given in **bold**. Every setting is in the Settings window (tray icon → *Settings…*) and can also be changed with `wclop settings set <name> <value>` (see [Command line](#command-line)).

- [How optimisation works](#how-optimisation-works)
- [Images](#images) · [Video](#video) · [PDF](#pdf) · [Audio](#audio)
- [Clipboard](#clipboard)
- [Drop zone](#drop-zone)
- [Watched folders](#watched-folders)
- [Result cards](#result-cards)
- [Hotkeys](#hotkeys)
- [Fit under a size](#fit-under-a-size)
- [Converting](#converting)
- [Batch mode](#batch-mode)
- [Pipelines](#pipelines)
- [Explorer and Send To](#explorer-and-send-to)
- [Command line](#command-line)
- [Where files go](#where-files-go)
- [Backups, restore and safety](#backups-restore-and-safety)
- [Tray icon](#tray-icon)
- [Settings window](#settings-window)
- [Updates](#updates)
- [Installer](#installer)
- [Privacy](#privacy)

---

## How optimisation works

Every file goes through the same steps, whatever started it (clipboard, drop zone, folder, Explorer, command line):

1. **Skip what's done.** Files WClop has already optimised carry a marker (an NTFS alternate data stream, with a database fallback on other drives) and aren't processed again automatically.
2. **Back up the original** into the working folder, named by a hash of its content.
3. **Reuse earlier work.** If the same content was optimised with the same settings before, the cached result is used instantly.
4. **Optimise** with the right engine for the file type (below).
5. **Never larger.** If the result isn't smaller, the original is kept and the card says *Already fully compressed*.
6. **Place the result** where you want it (in place by default; see [Where files go](#where-files-go)), keeping the original's dates.
7. **Mark it** so it isn't picked up again, and show a result card.

The **compression factor** (5–100, **30** for images, **50** for video, **35** for audio) sets how hard WClop squeezes: higher means smaller files with more loss. *Aggressive* uses 64. Adjustments always start again from the original, so stepping down several times never loses quality twice.

## Images

| Input | What happens |
| --- | --- |
| PNG | Lossy palette reduction with pngquant; quality ceiling and palette size scale with the factor. |
| JPEG | Recompressed with jpegoptim; quality falls as the factor rises. |
| GIF | Optimised with gifsicle, animation kept (from factor 80 up, every 2nd–4th frame is dropped on GIFs with more than 8 frames). A result that lost its animation is rejected. |
| WebP, AVIF, HEIC, BMP | Converted to JPEG by default (**Compression → convert to JPEG**), since those formats paste badly into most apps. |
| TIFF | Converted to PNG by default. |

- **Metadata:** camera, location and editing metadata is stripped (**on**); colour profiles are kept (**on**) so colours don't shift.
- **Downscaling** uses Windows' own high-quality decoder scaling; animated GIFs are scaled frame by frame with gifsicle. Steps go 100% → 75% → 50% → 40% → 30% → 20% → 10%.
- Every output is decoded once to check it's a valid image before it replaces anything.

## Video

- **Encoder:** WClop tests the GPU encoders your PC actually has, in order: NVIDIA NVENC, Intel Quick Sync, AMD AMF, then Windows' Media Foundation encoder, and falls back to x264 (software) if none work. The result is remembered until WClop restarts.
- **Quality tiers:** Adaptive, Lossless, **Fast**, Smaller, Custom (Compression tab).
- **Frame rate:** capped at **60 fps** (**on**), which shrinks high-refresh screen recordings without visible change.
- **Audio:** kept by default; *Remove audio from videos* drops it.
- **Conversions:** MOV, MPEG and WebM become MP4 by default.
- **Screen recordings:** files still being written are waited for, and only processed once the recorder has finished them.
- **Speed up** a video from its card or with the hotkey: 1.25×, 1.5×, 1.75×, 2×, then 3× … 10×.

## PDF

PDFs are rewritten with Ghostscript, recompressing the images inside them.

- **Adaptive DPI (default):** WClop looks at the resolution of every image in the PDF and picks a target that keeps the document sharp, ignoring outliers such as a single huge logo.
- **Fixed DPI:** always use one resolution (**150**).
- **Step down** the DPI from a PDF's card (the − button or hotkey).
- Text, fonts and links are kept; encrypted PDFs are left alone.

## Audio

Re-encoded with FFmpeg at a bitrate set by the factor, never above the source's own bitrate. WAV and MP3 become MP3; OGG/Opus stays Opus; FLAC, AIFF, M4A and AAC become AAC (M4A). Cover art is kept in MP3 and M4A. Audio in watched folders is **off** by default.

## Clipboard

Copy an image anywhere and WClop replaces it on the clipboard with the optimised version, usually within a second.

- **What it puts back:** the image itself (for apps that paste pixels) plus a reference to the optimised file (**on**, *also copy as a file*) for apps that prefer files, such as chat apps and upload boxes. Other formats (WebP, AVIF, videos) go back as a file.
- **What it leaves alone:**
  - rich content from apps like Word, Excel, PowerPoint, Photoshop and design tools (anything with RTF, OLE objects, SVG or similar alongside the image), so pasting back into those apps still works;
  - content from apps on your **ignored apps** list (password managers, for example);
  - content redirected from Remote Desktop or VMs (**on**);
  - WClop's own writes, and clipboard managers re-copying them.
- **Also optimise:** copied image files (off), videos, PDFs and audio files (off). Use *Optimise the clipboard now* (Ctrl+Alt+Shift+Z) to process anything on demand, including copied image **links**, which are downloaded first, and base64 image text.
- **Clipboard history:** results appear in Win+V history (**on**).
- **Screenshots:** the Snipping Tool both copies a screenshot and saves it to Pictures\Screenshots. WClop recognises it's the same image and shows one card, not two, while still optimising the saved file.
- **Skip the next copy:** the tray menu or Ctrl+Alt+Shift+P lets one copy through untouched.

## Drop zone

Drag files towards the edge of the screen and a translucent tab slides out.

- **Drop** to optimise. Hold **Alt** to keep the original and save the result next to it; hold **Ctrl** to optimise aggressively.
- **Scroll while holding the files** over the tab to pick a preset. The dots show where you are:
  - Normal (your settings)
  - Aggressive
  - Maximum
  - Half size
  - Under 10 MB (Discord's limit)
  - Under 25 MB (email)
  - Gentle
  - then any saved [pipelines](#pipelines) meant for the files you're dragging
- **What you can drop:** files, folders, image links from a browser, images dragged out of browsers and chat apps, Outlook attachments and other virtual files.
- **Folders and big drops** (a folder, or more than **30** files) open [batch mode](#batch-mode) instead of a pile of cards.
- **Position:** right edge by default. *Settings → Results → Drop zone* picks the edge and height, and *Move it on screen…* lets you drag it into place.
- It's hidden when no drag is happening and doesn't appear in screenshots.

## Watched folders

New files in these folders are optimised automatically.

| Kind | Default folders | On | Limits |
| --- | --- | --- | --- |
| Images | Pictures\Screenshots, OneDrive\Pictures\Screenshots | **Yes** | 50 KB – 50 MB, at least 20 px |
| Videos | Videos\Screen Recordings, Videos\Captures | **Yes** | 200 KB – 500 MB, at least 50 px |
| PDFs | none | Yes, once you add a folder | up to 100 MB |
| Audio | none | **No** | up to 100 MB |

- Only files directly in the folder count, not subfolders, and only new or renamed files (not ones a sync tool touches).
- **Bursts:** if more new files than the limit appear at once (unzipping, copying a folder in), they're left alone and you get a notice.
- **First run:** for 30 seconds after WClop's very first launch, it checks that no other app is rewriting the folder constantly, and turns the watcher off (with a notice) if one is.
- **Skip formats** per kind (TIFF for images, MKV/M4V for videos by default).
- **Ignore files:** a `.wclopignore-image` (or `-video`, `-pdf`, `-audio`) file in a folder lists patterns to skip, gitignore style.
- **Quiet folders:** tick *Don't show result cards* for a folder to optimise silently.
- A folder on a drive that isn't connected is remembered and picked up again when it is.

## Result cards

Each job shows a card in a corner of the screen (**bottom right**, on the screen the mouse is on).

- **Shows:** a thumbnail, the file name, *old → new size (−%)*, dimensions, and progress while working.
- **Buttons:**
  - **Dismiss**
  - **Restore** the original
  - **Fit under a size** (512 KB … 100 MB; only sizes below the original)
  - **Convert to** another format
  - **Show in Explorer**
  - **Run a pipeline** (the lightning bolt, when you have saved pipelines that suit the file)
- **Drag the thumbnail** into any app to use the file.
- **Hover** a card to keep it, and to point the hotkeys at it.
- **Auto-hide** after **30 s** (**10 s** for clipboard results).
- **Hidden from screenshots** by default, so they don't end up in the screenshot you're taking.
- Dismissed cards can be brought back (Ctrl+Alt+Shift+=).

## Hotkeys

All use **Ctrl+Alt+Shift** by default. The modifiers and every key can be changed in *Settings → Hotkeys*, which also shows when another app has taken a combination.

| Key | Action |
| --- | --- |
| Z | Optimise whatever is on the clipboard now (images, image files, links, base64) |
| A | Optimise the hovered or newest result aggressively |
| − | Downscale one step (75%, 50%, 40% …); for PDFs, lower the DPI |
| 1–9 | Downscale to 10%–90% |
| U | Restore the original |
| X | Speed up a video |
| P | Running → skip next copy → stopped |
| Delete | Dismiss the newest result |
| = | Bring back the last dismissed result |
| Escape | Clear all results and stop running jobs |
| Space | Open the result in your default viewer |

Hotkeys act on the card under the mouse, else the newest result, else what's on the clipboard.

## Fit under a size

Makes a file fit under a byte limit, from a card, a drop-zone preset, a pipeline (`targetSize(10MB)`) or `wclop optimise --fit 10MB`. It always works from the original, and if the target can't be reached, the smallest attempt is kept and the card says so.

- **Images:** aggressive compression first, then downscaling by the square root of how far over the limit it is, up to five times.
- **Video:** the bitrate is worked out from the target and the duration (leaving room for audio). Very low bitrates also drop to 30 fps and then downscale, with one retry if the file comes out over.
- **PDF:** a search over DPI from 250 down to 48, keeping the sharpest result that fits.
- **Audio:** the bitrate from the target and duration.

## Converting

From a card (*Convert to…*), a pipeline (`convert(to: webp)`) or `wclop optimise --to webp`. Conversion always starts from the original.

- **Images:** JPEG, PNG, WebP, AVIF, GIF.
- **Videos:** animated GIF, WebM (VP9) and MP4 (HEVC, smaller).
- JPEG, PNG and GIF outputs are optimised afterwards.
- The converted file is saved next to the original by default; restoring removes it.

## Batch mode

For folders and big selections: tray → *Batch optimise a folder…*, dropping a folder, Explorer's *Optimise folder with WClop*, or `wclop batch`.

- A table of every file with its type, status, before, after and saving, plus running totals; you can sort by any column.
- **Every original is backed up first** into `batch-backups\batch-<time>`, which is never cleaned up automatically.
- **Options:** the preset (the drop-zone presets), keep originals, include subfolders.
- **Stop**, **Restore all** (puts back byte-identical originals) and **Open backups**.
- Several files are processed in parallel, but videos one at a time.

## Pipelines

A pipeline is a chain of steps run on a file. Write them in *Settings → Pipelines* (with live checking, examples and a full step reference), or with `wclop pipeline add`.

```
if(regex: "^screenshot (\d+)") -> downscale(longEdge: 1920) -> convert(webp) -> rename(to: "shot-$1")
```

**Syntax:**
- Steps are joined by `->`.
- Values are named, as in `step(name: value)`; a single unnamed value goes to the step's main argument, so `convert(webp)` means `convert(to: webp)`.
- Values can be sizes (`10MB`), percentages (`50%`), ratios (`"16:9"`) or quoted text; `#` starts a comment.
- Mistakes are reported with the line and column, and a *did you mean* suggestion.

**Steps:**

| Step | What it does |
| --- | --- |
| `optimise(factor, aggressive, dpi)` | WClop's normal optimisation |
| `downscale(factor \| width \| height \| longEdge)` | Shrink images and videos; never enlarges |
| `convert(to)` | jpeg, png, webp, avif, gif, webm, mp4 |
| `crop(width, height, aspectRatio, smart)` | Crop from the centre; `smart: true` keeps the most detailed part of an image |
| `targetSize(size)` | Fit under a size |
| `watermark(image, position, opacity, scale, margin)` | Overlay a logo on images, videos (every frame, audio kept) and animated GIFs: `bottomRight` (default), `bottomLeft`, `topRight`, `topLeft` or `center`; width as a share of the file's (**15%**), **20 px** from the edges; transparency is kept. Without `image`, it uses the default watermark from *Settings → Pipelines* |
| `stripExif` | Remove metadata |
| `changeSpeed(factor)` · `removeAudio` · `capFps(fps)` | Video |
| `lowerBitrate(kbps)` · `normalize(lufs)` | Video and audio (loudness to −16 LUFS by default) |
| `copy(to)` · `move(to)` · `rename(to)` · `delete` | Files: `~` is your user folder; `%f %e %P %y %m %d %H %M %S %r %i` are name, extension, parent, date and time tokens; a `to` ending in `\` or `/` is a folder; `delete` uses the Recycle Bin |
| `if(…)` · `ifNot(…)` | Continue only if: `type`, `regex` (captures become `$1`, `${name}`), `nameContains`, `nameIs`, `sizeGreaterThan` / `sizeLowerThan`, `width…` / `height…`, `copiedBy` (the app it was copied from), `source` |
| `extractPagesAsImages(format, quality, dpi, to)` | Save every PDF page as JPEG or PNG |
| `fork("steps" or a saved pipeline)` | Run steps on a copy, then carry on with the original |
| `runScript(path \| code, shell)` | Run an .exe, .ps1, .bat, .py or .js, or inline PowerShell or cmd, with the file as the first argument and in `%WCLOP_INPUT_FILE%`. If it prints the path of a new file of the same kind, the next steps use that file. |
| `copyToClipboard(as)` | Put the result on the clipboard as a file, image, path or Markdown link |
| `openWith(app)` | Open the result in an app |

**Where pipelines run:**
- From a result card (the lightning bolt).
- As drop-zone presets (scroll to them).
- *Try it on a file…* in Settings.
- `wclop pipeline run <name or steps> <files>`.
- Automatically, when attached to copied images, drops, or any watched folder.

**Options per pipeline:**
- *Don't optimise first*, for pipelines that re-encode anyway.
- *No result card* when it runs automatically.
- Offer it in the drop zone, and on result cards.
- **Only for** images, videos, PDFs or audio.

**Safety:**
- The original is backed up before the first step, so *Restore* always gets it back.
- A step that doesn't apply to the file's type is skipped.
- A failing script writes a log with its output to the working folder.

`wclop pipeline prompt` prints a complete reference of the language that you can give an AI assistant so it can write pipelines for you.

## Explorer and Send To

- **Right-click → Optimise with WClop** on any supported file, and **Optimise folder with WClop** on folders. On Windows 11 these are under *Show more options*.
- **Send to → WClop (optimise).**
- Selecting several files sends them as one job; a folder or more than 30 files opens batch mode.
- Turn either on or off in *Settings → General*. The installer turns both on.

## Command line

`wclop` talks to the running app, so its results show up as cards and share the app's cache and backups. If the app isn't running, it does the work itself.

```
wclop optimise <files or folders> [--preset <name>] [--factor 1-100] [--scale 50%] [--fit 10MB]
                                  [--to webp] [--keep] [--output temp|inplace|same|specific]
                                  [--allow-larger] [--no-wait] [--local]
wclop batch <files or folders>
wclop stop
wclop settings list | get [name] | set <name> <value> | show
wclop pipeline list | show | add | delete | check | run | attach | detach | prompt
```

Examples:

```
wclop optimise video.mp4 --fit 25MB
wclop optimise *.png --to webp --keep
wclop settings set compression.imageFactor 45
wclop pipeline add Web "downscale(longEdge: 1920) -> convert(webp)" --skip-optimise
wclop pipeline attach Web folder "%USERPROFILE%\Pictures\Screenshots"
```

Other programs can use the same local API: three named pipes (optimise, stop, settings), reachable only by your Windows account, taking one JSON line in and giving one out.

## Where files go

Set per file type in *Settings → Output*, separately for optimised files, automatic conversions and manual conversions:

- **In place** (default): replace the original, which goes to backups.
- **Same folder**: save next to it, named by a template (**`%f-optimised`**).
- **Specific folder**: a template such as **`%P/optimised/%f`**.
- **Temporary**: keep it in the working folder; the original is untouched.

Templates use `%f` (name), `%e` (extension), `%P` (parent folder), `%y %m %d %H %M %S` (date and time), `%r` (random) and `%i` (counter), plus environment variables. The Output tab shows a live preview.

## Backups, restore and safety

- Originals are backed up to `%LOCALAPPDATA%\WClop\Cache\backups` before being replaced, and **Restore** (card, hotkey, pipeline results too) puts them back, under their original name and dates.
- The working folder is cleaned of files older than **3 days** (choose 10 minutes to never), except batch backups.
- WClop never overwrites a file that isn't the original or its own output; it picks a new name instead.
- A file that's still being written, or that another program has open for a moment, is waited for rather than failed.
- WClop checks its bundled tools against their checksums at startup and tells you if any were damaged or changed.
- Errors are logged to `%LOCALAPPDATA%\WClop\logs\wclop.log` (rotated at 5 MB); *Open log* is in the tray menu.

## Tray icon

The clipboard-with-a-W icon's colour shows the state: **blue** running, **amber** skipping the next copy, **grey** stopped. Click it to open Settings; the state can be changed from its menu or with Ctrl+Alt+Shift+P.

The menu has:
- Settings
- the state options
- Optimise copied images
- Optimise new images in watched folders
- the hotkey list with each key's status
- Clear results
- Batch optimise a folder…
- Open working folder
- Open settings file
- Open log
- Log clipboard formats (a troubleshooting option)
- Exit

## Settings window

Changes apply as you make them.

- **General:** start when you sign in, Explorer menu, Send To, updates, working folder, cleanup interval, version.
- **Clipboard:** what to optimise, ignored apps (pick from running apps), extra formats to leave alone, clipboard history, Remote Desktop.
- **Watched folders:** folders per kind, limits, skipped formats, quiet folders.
- **Compression:** factor slider, video tier and encoder, fps cap, audio, PDF DPI, automatic conversions, metadata.
- **Output:** where files go, name templates with a preview.
- **Results:** card corner and screen, auto-hide times, drag out, visibility in screenshots, drop zone and its position.
- **Hotkeys:** modifiers, each key on or off, remapping, conflicts.
- **Pipelines:** everything in [Pipelines](#pipelines), plus the default watermark image.

Settings are stored in `%APPDATA%\WClop\settings.json`.

## Updates

WClop looks for a new version in its GitHub releases at startup and once a day (*Check for new versions automatically*, **on**).

- **When one is found:** a notification appears, the tray menu gets an *Install WClop x.y.z…* item, and *Settings → General → Updates* shows the new version with a link to what's new and a **Download and install** button.
- **Installing:**
  - the installer is downloaded and checked against the release's SHA-256 before it runs;
  - WClop closes, the new version is installed over it and starts again;
  - your settings, and your choices for start at sign-in and the Explorer menu, are kept.
- **Install new versions automatically** (off) installs as soon as an update is found, waiting until no jobs are running.
- **Check now** checks straight away.
- Only a copy installed with the installer updates itself. A copy run from anywhere else points you to the release page instead.

## Installer

- **Per user:** no administrator rights needed. It installs to `%LOCALAPPDATA%\Programs\WClop`, adds a Start menu entry, puts `wclop` on your PATH, and sets up start at sign-in, the Explorer menu and Send To.
- **Silent installs** (`msiexec /i WClop.msi /qn`) accept `LAUNCHATLOGIN=0`, `EXPLORERMENU=0` and `LAUNCHAPP=0`.
- **Updating:** WClop can do it for you (see [Updates](#updates)), or install the newer MSI yourself. The running WClop is closed, updated and started again, and your settings are kept.
- **Uninstalling** (Settings → Apps) removes the program, shortcuts, PATH entry, Explorer menu, Send To and start at sign-in. Your settings and working folder stay in `%APPDATA%\WClop` and `%LOCALAPPDATA%\WClop`; delete them by hand for a completely clean removal.

## Privacy

WClop works entirely on your PC and has no telemetry. It only goes online to:

- check GitHub for a new version (the request carries nothing about you or your files; turn it off under Updates);
- download an update you chose to install;
- download an image from a link you asked it to optimise.
