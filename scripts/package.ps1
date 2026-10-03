<#
.SYNOPSIS
    Builds the WClop installer: artifacts\WClop-<version>-<arch>.msi (x64 by default, or -Arch arm64).

.DESCRIPTION
    1. Runs the tests (skip with -SkipTests).
    2. Publishes WClop.exe and wclop.exe self-contained (no .NET install needed) for win-<arch> into artifacts\publish
       (artifacts\publish-arm64 for arm64).
    3. Copies the external tools (scripts/fetch-tools.ps1 -Arch <arch> must have run; tools\ for x64, tools-arm64\ for
       arm64) into publish\tools, with the Visual C++ runtime Ghostscript needs (app-local, as the VC++ redistributable
       licence allows), and writes tools-manifest.json with every tool's SHA-256; WClop checks it at startup.
       The arm64 build's ffmpeg/ffprobe are native; its other tools are x64 and run under emulation, so they keep the
       x64 VC++ runtime too.
    4. Signs the executables and the MSI when a certificate is given (-CertThumbprint or $env:WCLOP_SIGN_THUMBPRINT,
       a code-signing certificate in the user's certificate store). Unsigned builds work, but SmartScreen warns and
       antivirus is more suspicious of them.
    5. Builds the MSI with WiX 5 (a local dotnet tool: dotnet tool restore).

    -PublishOnly stops after step 3 and -MsiOnly does only step 5 (and its signing) from an existing publish folder,
    so something else can sign the binaries in between: the release workflow does that with Azure Artifact Signing.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [ValidateSet('x64', 'arm64')]
    [string] $Arch = 'x64',
    [string] $CertThumbprint = $env:WCLOP_SIGN_THUMBPRINT,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipTests,
    [switch] $PublishOnly,
    [switch] $MsiOnly
)

$ErrorActionPreference = 'Stop'
if ($PublishOnly -and $MsiOnly) { throw '-PublishOnly and -MsiOnly are the two halves of a build: pass one of them' }
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
# x64 keeps the original names (the tests look for artifacts\publish\tools).
$suffix = if ($Arch -eq 'x64') { '' } else { "-$Arch" }
$publish = Join-Path $artifacts "publish$suffix"

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "WClop $Version ($Arch)"

function Invoke-Checked([string] $what, [scriptblock] $command) {
    Write-Host "== $what"
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Sign([string[]] $files) {
    if (-not $CertThumbprint) { return }
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    Invoke-Checked "Sign $($files.Count) file(s)" { & $signtool sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /q @files }
}

if (-not $MsiOnly) {
    $tools = Join-Path $root "tools$suffix"
    foreach ($tool in 'pngquant.exe', 'jpegoptim.exe', 'gifsicle.exe', 'exiftool.exe', 'ffmpeg.exe', 'ffprobe.exe', 'gswin64c.exe', 'gsdll64.dll') {
        if (-not (Test-Path (Join-Path $tools $tool))) { throw "tools$suffix\$tool is missing: run scripts\fetch-tools.ps1 -Arch $Arch first" }
    }

    if (-not $SkipTests) {
        Invoke-Checked 'Tests' { dotnet test (Join-Path $root 'WClop.Tests\WClop.Tests.csproj') -c Release --nologo -v q }
    }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    # Each project publishes into its own staging folder, then the CLI's files are merged in: publishing both into one
    # folder lets the SDK's "remove files from the previous publish" step delete the other project's files.
    $staging = Join-Path $artifacts "staging$suffix"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $common = @('-c', 'Release', '-r', "win-$Arch", '--self-contained', 'true', "-p:Version=$Version", '--nologo', '-v', 'q')
    Invoke-Checked 'Publish WClop' { dotnet publish (Join-Path $root 'WClop\WClop.csproj') @common -o $publish }
    # Windows file names ignore case, so the CLI can't be wclop.exe next to WClop.exe: it ships as wclop-cli.exe (sharing
    # the app's runtime) and a cli\wclop.cmd shim, the only thing on PATH, keeps the command "wclop".
    Invoke-Checked 'Publish wclop (CLI)' {
        dotnet publish (Join-Path $root 'WClop.Cli\WClop.Cli.csproj') @common '-p:WClopCliAssemblyName=wclop-cli' -o (Join-Path $staging 'cli')
    }
    # Runtime files are identical in both; only add what the app folder doesn't have (the CLI's own files).
    Get-ChildItem (Join-Path $staging 'cli') -File | Where-Object { -not (Test-Path (Join-Path $publish $_.Name)) } |
        Copy-Item -Destination $publish
    Remove-Item $staging -Recurse -Force
    $cli = Join-Path $publish 'cli'
    New-Item -ItemType Directory -Force $cli | Out-Null
    Set-Content (Join-Path $cli 'wclop.cmd') "@`"%~dp0..\wclop-cli.exe`" %*`r`n@exit /b %ERRORLEVEL%" -Encoding ascii -NoNewline
    if (-not (Get-Content (Join-Path $publish 'WClop.runtimeconfig.json') -Raw).Contains('WindowsDesktop')) { throw 'WClop.exe was overwritten' }

    # Licence texts travel with the program (GPL: the licence and the bundled tools' notices).
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $publish 'THIRD-PARTY-NOTICES.md')

    Write-Host '== Tools'
    $publishTools = Join-Path $publish 'tools'
    Copy-Item $tools $publishTools -Recurse
    # Ghostscript is x64 in both builds, and so is this runtime: on an arm64 machine it loads into the emulated process.
    foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
        Copy-Item (Join-Path $env:SystemRoot "System32\$dll") $publishTools
    }
    # Same format as WClop.Core.Processes.ToolManifest.
    $entries = Get-ChildItem $publishTools -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($publishTools.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    ConvertTo-Json @($entries) | Set-Content (Join-Path $publishTools 'tools-manifest.json') -Encoding utf8
    Write-Host "  $($entries.Count) files hashed"

    # WClop's own binaries (the release workflow signs the same list).
    Sign @('WClop.exe', 'WClop.dll', 'WClop.Core.dll', 'wclop-cli.exe', 'wclop-cli.dll' | ForEach-Object { Join-Path $publish $_ })
    if (-not $CertThumbprint) { Write-Warning 'Not signed: pass -CertThumbprint (or set WCLOP_SIGN_THUMBPRINT) to sign.' }

    if ($PublishOnly) {
        Write-Host "Published to $publish"
        return
    }
}

if (-not (Test-Path (Join-Path $publish 'WClop.exe'))) { throw "$publish has no WClop.exe: run without -MsiOnly first" }
$msi = Join-Path $artifacts "WClop-$Version-$Arch.msi"
Push-Location $root
try {
    Invoke-Checked 'WiX restore' { dotnet tool restore }
    Invoke-Checked 'Build MSI' {
        dotnet tool run wix build (Join-Path $root 'installer\WClop.wxs') -arch $Arch `
            -d "Version=$Version" -d "PublishDir=$publish" -d "IconPath=$(Join-Path $root 'WClop\Assets\WClop.ico')" `
            -o $msi
    }
}
finally {
    Pop-Location
}
Sign @($msi)

$size = [Math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Built $msi ($size MB)"
