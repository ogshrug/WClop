<#
.SYNOPSIS
    Downloads the external tools WClop drives into <repo>/tools (dev builds find them there).

.DESCRIPTION
    Fetches pngquant, jpegoptim, gifsicle, exiftool, ffmpeg/ffprobe and Ghostscript (Windows x64 builds).
    Existing tools are skipped unless -Force is passed.
    Licences: pngquant is GPLv3, gifsicle GPLv2, ffmpeg (gyan.dev essentials) GPLv3, Ghostscript AGPLv3,
    jpegoptim GPLv3, exiftool Perl Artistic/GPL.
#>
[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is very slow with the progress bar on.

$toolsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools'

# In CI, GITHUB_TOKEN lifts the GitHub API's anonymous rate limit (60 requests an hour per IP, shared by runners).
$githubHeaders = if ($env:GITHUB_TOKEN) { @{ Authorization = "Bearer $env:GITHUB_TOKEN" } } else { @{} }
New-Item -ItemType Directory -Force $toolsDir | Out-Null

$staging = Join-Path ([IO.Path]::GetTempPath()) ("wclop-tools-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $staging | Out-Null

function Get-ZipAndExtract([string] $name, [string] $url) {
    $zip = Join-Path $staging "$name.zip"
    $dest = Join-Path $staging $name
    Write-Host "  downloading $url"
    # curl.exe (built into Windows 10+) rather than Invoke-WebRequest: SourceForge serves an HTML page
    # instead of the file to PowerShell's browser-like user agent.
    & curl.exe --fail --silent --show-error --location --output $zip $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $url" }
    $magic = [IO.File]::ReadAllBytes($zip)[0..1]
    if ($magic[0] -ne 0x50 -or $magic[1] -ne 0x4B) { throw "Not a zip file: $url" }
    Expand-Archive -Path $zip -DestinationPath $dest -Force
    return $dest
}

function Copy-Exe([string] $extractedDir, [string] $sourceName, [string] $targetName) {
    $exe = Get-ChildItem -Path $extractedDir -Recurse -File -Filter $sourceName | Select-Object -First 1
    if (-not $exe) { throw "$sourceName not found in the downloaded archive" }
    Copy-Item $exe.FullName (Join-Path $toolsDir $targetName) -Force
    return $exe
}

function Test-Needed([string[]] $exeNames) {
    if ($Force) { return $true }
    foreach ($exe in $exeNames) {
        if (-not (Test-Path (Join-Path $toolsDir $exe))) { return $true }
    }
    return $false
}

try {
    if (Test-Needed 'pngquant.exe') {
        Write-Host 'pngquant'
        $dir = Get-ZipAndExtract 'pngquant' 'https://pngquant.org/pngquant-windows.zip'
        Copy-Exe $dir 'pngquant.exe' 'pngquant.exe' | Out-Null
    }

    if (Test-Needed 'jpegoptim.exe') {
        Write-Host 'jpegoptim'
        $release = Invoke-RestMethod 'https://api.github.com/repos/tjko/jpegoptim/releases/latest' -Headers $githubHeaders
        $asset = $release.assets | Where-Object { $_.name -like '*x64-windows.zip' } | Select-Object -First 1
        if (-not $asset) { throw "No Windows x64 asset in jpegoptim release $($release.tag_name)" }
        $dir = Get-ZipAndExtract 'jpegoptim' $asset.browser_download_url
        Copy-Exe $dir 'jpegoptim.exe' 'jpegoptim.exe' | Out-Null
    }

    if (Test-Needed 'gifsicle.exe') {
        Write-Host 'gifsicle'
        $dir = Get-ZipAndExtract 'gifsicle' 'https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.95-win64.zip'
        Copy-Exe $dir 'gifsicle.exe' 'gifsicle.exe' | Out-Null
    }

    if (Test-Needed 'exiftool.exe') {
        Write-Host 'exiftool'
        $version = (Invoke-WebRequest 'https://exiftool.org/ver.txt' -UseBasicParsing).Content.Trim()
        $dir = Get-ZipAndExtract 'exiftool' "https://downloads.sourceforge.net/project/exiftool/exiftool-${version}_64.zip"
        # The Windows build ships as "exiftool(-k).exe" (the -k pauses at exit); renaming it removes the pause.
        # It needs its exiftool_files folder alongside.
        $exe = Copy-Exe $dir 'exiftool(-k).exe' 'exiftool.exe'
        $files = Join-Path $exe.DirectoryName 'exiftool_files'
        $target = Join-Path $toolsDir 'exiftool_files'
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $files $target -Recurse
    }

    if (Test-Needed 'ffmpeg.exe', 'ffprobe.exe') {
        Write-Host 'ffmpeg (large download)'
        $dir = Get-ZipAndExtract 'ffmpeg' 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
        Copy-Exe $dir 'ffmpeg.exe' 'ffmpeg.exe' | Out-Null
        Copy-Exe $dir 'ffprobe.exe' 'ffprobe.exe' | Out-Null
    }

    if (Test-Needed 'gswin64c.exe', 'gsdll64.dll') {
        Write-Host 'Ghostscript (AGPL; large download)'
        $release = Invoke-RestMethod 'https://api.github.com/repos/ArtifexSoftware/ghostpdl-downloads/releases/latest' -Headers $githubHeaders
        $asset = $release.assets | Where-Object { $_.name -like 'gs*w64.exe' } | Select-Object -First 1
        if (-not $asset) { throw "No Windows x64 installer in Ghostscript release $($release.tag_name)" }
        $installer = Join-Path $staging 'gs-setup.exe'
        & curl.exe --fail --silent --show-error --location --output $installer $asset.browser_download_url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($asset.browser_download_url)" }

        # The installer is an NSIS archive: unpack it with 7-Zip rather than running it (no admin, nothing installed).
        $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
        if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
        if (-not $sevenZip) {
            # A per-user "administrative install" of the 7-Zip MSI just extracts its files.
            $msi = Join-Path $staging '7zip.msi'
            & curl.exe --fail --silent --show-error --location --output $msi 'https://www.7-zip.org/a/7z2501-x64.msi'
            if ($LASTEXITCODE -ne 0) { throw 'Download of 7-Zip failed' }
            Start-Process msiexec.exe -ArgumentList '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$staging\7zip`"" -Wait
            $sevenZip = Get-ChildItem "$staging\7zip" -Recurse -Filter 7z.exe | Select-Object -First 1 -ExpandProperty FullName
            if (-not $sevenZip) { throw '7-Zip could not be extracted' }
        }

        & $sevenZip x -y "-o$staging\gs" $installer | Out-Null
        foreach ($file in 'gswin64c.exe', 'gsdll64.dll') {
            Copy-Item (Join-Path "$staging\gs\bin" $file) (Join-Path $toolsDir $file) -Force
        }
    }

    Write-Host "Tools are in $toolsDir"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
