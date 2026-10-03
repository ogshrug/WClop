<#
.SYNOPSIS
    Writes the winget manifests for a release from packaging\winget into artifacts\winget\manifests\o\ogshrug\WClop\<version>.

.DESCRIPTION
    Fills in the version, each installer's SHA-256 and its MSI ProductCode (new in every build) from the MSIs in
    artifacts\ (scripts/package.ps1's output), or with -FromRelease from the published GitHub release, so the hashes
    match what users download. A release without the arm64 installer gets an x64-only manifest.
    Needed for the first submission to microsoft/winget-pkgs (docs/RELEASING.md); after that the release workflow's
    winget job (winget-releaser) updates the package on its own.
    Check the result with: winget validate --manifest <the folder it prints>
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $FromRelease,
    [string] $Repository = 'ogshrug/WClop'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

function Get-MsiProperty([string] $msi, [string] $name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property = '$name'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    foreach ($com in $record, $view, $database, $installer) { [Runtime.InteropServices.Marshal]::ReleaseComObject($com) | Out-Null }
    return $value
}

$download = Join-Path $artifacts 'winget-download'
$values = @{ Version = $Version; ReleaseDate = (Get-Date).ToString('yyyy-MM-dd') }
$found = @{}
foreach ($arch in 'x64', 'arm64') {
    $name = "WClop-$Version-$arch.msi"
    if ($FromRelease) {
        New-Item -ItemType Directory -Force $download | Out-Null
        $msi = Join-Path $download $name
        & curl.exe --fail --silent --location --output $msi "https://github.com/$Repository/releases/download/v$Version/$name"
        if ($LASTEXITCODE -ne 0) { Write-Host "  (no $name in the release)"; Remove-Item $msi -ErrorAction SilentlyContinue; continue }
    } else {
        $msi = Join-Path $artifacts $name
        if (-not (Test-Path $msi)) { continue }
    }
    $key = if ($arch -eq 'x64') { 'X64' } else { 'Arm64' }
    $values["${key}Sha256"] = (Get-FileHash $msi -Algorithm SHA256).Hash
    $values["${key}ProductCode"] = Get-MsiProperty $msi 'ProductCode'
    $found[$arch] = $true
    Write-Host "  $name  $($values["${key}ProductCode"])"
}
if (-not $found['x64']) { throw "No WClop-$Version-x64.msi found (build it with scripts\package.ps1, or pass -FromRelease)" }

$out = Join-Path $artifacts "winget\manifests\o\ogshrug\WClop\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($template in Get-ChildItem (Join-Path $root 'packaging\winget') -Filter *.yaml) {
    $lines = Get-Content $template.FullName | Where-Object { $_ -notmatch '^# Template:' }
    if (-not $found['arm64']) {
        # The arm64 installer entry runs from its comment line to the line before ManifestType.
        $start = [Array]::FindIndex([string[]] $lines, [Predicate[string]] { param($l) $l -match '^# arm64' })
        $end = [Array]::FindIndex([string[]] $lines, [Predicate[string]] { param($l) $l -match '^ManifestType:' })
        if ($start -ge 0) { $lines = $lines[0..($start - 1)] + $lines[$end..($lines.Count - 1)] }
    }
    $text = ($lines -join "`n").TrimStart("`n") + "`n"
    foreach ($key in $values.Keys) { $text = $text.Replace("{{$key}}", $values[$key]) }
    if ($text -match '\{\{\w+\}\}') { throw "$($template.Name): $($Matches[0]) wasn't filled in" }
    [IO.File]::WriteAllText((Join-Path $out $template.Name), $text)
}
Write-Host "Manifests are in $out"
