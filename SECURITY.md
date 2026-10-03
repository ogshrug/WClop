# Security policy

## Supported versions

Only the [latest release](https://github.com/ogshrug/WClop/releases/latest) gets security fixes. WClop updates itself (*Settings → General → Updates*), so please check you're on the newest version before reporting.

## Reporting a vulnerability

**Please don't open a public issue for security problems.**

Report them privately through GitHub: go to the repository's **Security** tab and choose **[Report a vulnerability](https://github.com/ogshrug/WClop/security/advisories/new)**. Include:

- the WClop version and Windows version;
- what an attacker could do, and what they need first (for example, a crafted file the user optimises, or code already running as the user);
- steps to reproduce it, or a proof of concept.

You should get a reply within a week. Once a fix is released, the advisory is published with credit to you, unless you'd rather stay anonymous.

## What's in scope

Things that count as security issues include:

- a file that makes WClop, or one of its bundled tools, run code or write outside the places it should;
- the local API (named pipes) or the MCP server (`wclop mcp`) being reachable by other users on the PC, or letting an AI assistant run scripts or programs when that isn't allowed;
- the updater installing something other than the genuine release, or skipping its SHA-256 check;
- WClop losing or overwriting a file without a backup.

Bugs in the bundled tools themselves (FFmpeg, Ghostscript, ExifTool, pngquant, jpegoptim, gifsicle) should also be reported to those projects. Please tell us too, so we can update the bundled version.

## How WClop limits risk

- Everything runs locally as your user; the installer needs no administrator rights.
- The named pipes are restricted to your Windows account.
- Bundled tools are checked against their checksums at startup, and updates against the release's SHA-256 before they run.
- AI assistants can't run pipelines with scripts or programs, change that setting, or attach pipelines to run automatically unless you allow it.
