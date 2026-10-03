# Contributing to WClop

Thanks for helping. Bug reports, feature ideas, documentation fixes and code are all welcome.

By taking part you agree to follow the [code of conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

[Open a bug report](https://github.com/ogshrug/WClop/issues/new?template=bug_report.yml). The most useful reports include:

- the WClop version (*Settings → General*) and your Windows version;
- what you did, what you expected, and what happened instead;
- the log, `%LOCALAPPDATA%\WClop\logs\wclop.log` (tray menu → *Open log*). Look it over first: it contains file paths;
- for clipboard problems, which app you copied from. Tray menu → *Log clipboard formats* writes the formats of the next copy to the log, which shows what WClop saw;
- for a file that fails to optimise, the file itself if you can share it.

Security problems go through [SECURITY.md](SECURITY.md) instead, not public issues.

## Suggesting a feature

[Open a feature request](https://github.com/ogshrug/WClop/issues/new?template=feature_request.yml) and describe the problem it solves before the solution you have in mind. If Clop for macOS already does it, say so: WClop follows Clop's behaviour where it makes sense on Windows.

For a large change, please open an issue to discuss it before writing the code, so your time isn't spent on something that can't be merged.

## Building from source

You need Windows 10 or 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the exact version is pinned in `global.json`) and PowerShell 7.

```powershell
git clone https://github.com/ogshrug/WClop.git
cd WClop
./scripts/fetch-tools.ps1                     # pngquant, jpegoptim, gifsicle, ExifTool, FFmpeg, Ghostscript into tools/ (~250 MB)
dotnet build WClop.sln
dotnet test WClop.Tests/WClop.Tests.csproj
dotnet run --project WClop                    # starts the tray app
```

Debug builds find the tools in the repository's `tools/` folder, so there's nothing to install. If you already have WClop installed, exit it from the tray before running a development build: both use the same settings and named pipes.

To build the installer: `./scripts/package.ps1` (into `artifacts/`). It needs no signing certificate.

## Project layout

| Folder | What's in it |
| --- | --- |
| `WClop/` | The WPF tray app: clipboard watching, drop zone, result cards, settings window, hotkeys, batch window, Explorer integration, updates |
| `WClop.Core/` | Everything that isn't UI: the image, video, PDF and audio engines, pipelines, storage and backups, settings, the local IPC protocol |
| `WClop.Cli/` | The `wclop` command and its MCP server (`wclop mcp`) |
| `WClop.Tests/` | xUnit tests |
| `installer/` | The WiX 5 installer |
| `scripts/` | `fetch-tools`, `package`, `winget-manifest`, `make-icon` |
| `packaging/` | winget and Scoop manifests |
| `docs/` | [FEATURES.md](docs/FEATURES.md) (user documentation) and [RELEASING.md](docs/RELEASING.md) (for maintainers) |

Logic that can be tested without a window belongs in `WClop.Core`, so it can be covered by tests; `WClop` should mostly wire it to the UI.

## Making a change

1. Fork the repository and create a branch from `main`.
2. Make your change, following the conventions below.
3. Add or update tests. Tests can run the real bundled tools (many do), so prefer testing with real files over mocks.
4. Update [docs/FEATURES.md](docs/FEATURES.md) if users will notice the change, and the README if it's a headline feature.
5. Check that `dotnet build WClop.sln` gives no new warnings and `dotnet test WClop.Tests/WClop.Tests.csproj` passes.
6. Open a pull request and fill in the template.

### Conventions

- **Match the code around you:** naming, comment density, file layout. C# uses file-scoped namespaces, nullable reference types and the latest language version (set in `Directory.Build.props`).
- **British spelling** in user-facing text and new identifiers: *optimise*, *colour*, *licence*. Settings search already understands American spellings, so users can still find things.
- **Write for users, not developers.** Settings labels, card messages and errors should say what happens in plain words.
- **Never lose a file.** Anything that replaces a file must back up the original first and must keep the original if the result is larger or invalid. Changes that touch this deserve tests.
- **Follow Clop** where it makes sense: if you're adding something Clop has, look at how Clop's documentation and changelog describe it, and keep pipeline step names compatible.
- **Tests go in a file named after what they test** (`CropTests.cs`, `ClipboardCollectionTests.cs`).
- **New dependencies:** a NuGet package or bundled tool must have a GPL-3.0-compatible licence, and goes into [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) in the same pull request.

### Commit messages

Write the subject in the imperative, as in the existing history (`Add the watermark pipeline step`), and keep it under about 72 characters. The body explains what changed and why, wrapped at about 76 characters.

## Licence

WClop is licensed under the [GNU General Public License v3.0 or later](LICENSE). By submitting a pull request, you agree that your contribution is licensed under the same terms.
