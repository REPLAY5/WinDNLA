#Requires -Version 5.1
<#
.SYNOPSIS
  Bump the last version component (+1), publish, and build MSI.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\installer\release.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\installer\release.ps1 -SkipBump
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipBump
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Parse-VersionParts([string]$raw) {
    $parts = @($raw.Trim() -split '\.' | Where-Object { $_ -ne '' })
    if ($parts.Count -lt 1) { throw "Cannot parse version: '$raw'" }
    if ($parts.Count -gt 4) { throw "Version '$raw' has more than 4 parts" }
    foreach ($p in $parts) {
        if ($p -notmatch '^\d+$') { throw "Cannot parse version: '$raw'" }
    }
    return @($parts | ForEach-Object { [int]$_ })
}

function Format-Version([int[]]$parts) {
    return ($parts -join '.')
}

function Get-FourPartVersion([string]$version) {
    $parts = @(Parse-VersionParts $version)
    while ($parts.Count -lt 4) { $parts += 0 }
    return Format-Version $parts[0..3]
}

function Step-PatchVersion([string]$version) {
    $parts = @(Parse-VersionParts $version)
    $parts[$parts.Count - 1] = $parts[$parts.Count - 1] + 1
    return Format-Version $parts
}

function Read-Element([string]$text, [string]$name, [string]$fallback) {
    $m = [regex]::Match($text, "(?s)<$name>\s*([^<]+?)\s*</$name>")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $fallback
}

function Set-Element([string]$text, [string]$name, [string]$value) {
    $pattern = "(<$name>)\s*[^<]+?\s*(</$name>)"
    if ([regex]::IsMatch($text, $pattern)) {
        return [regex]::Replace($text, $pattern, "`${1}$value`${2}")
    }
    throw "Element <$name> not found"
}

function Read-AppVersion {
    $path = Join-Path $Root "src\Directory.Build.props"
    if (-not (Test-Path $path)) { return "1.0.0.2" }
    $raw = [System.IO.File]::ReadAllText($path)
    return (Read-Element $raw "Version" "1.0.0.2").Trim()
}

function Write-AppVersion([string]$version) {
    $display = Format-Version (Parse-VersionParts $version)
    $four = Get-FourPartVersion $display

    $propsPath = Join-Path $Root "src\Directory.Build.props"
    $props = [System.IO.File]::ReadAllText($propsPath)
    $props = Set-Element $props "Version" $display
    $props = Set-Element $props "AssemblyVersion" $four
    $props = Set-Element $props "FileVersion" $four
    $props = Set-Element $props "InformationalVersion" $display
    [System.IO.File]::WriteAllText($propsPath, $props, $Utf8NoBom)

    $manifestPath = Join-Path $Root "src\WinDNLA.App\app.manifest"
    $manifest = [System.IO.File]::ReadAllText($manifestPath)
    $updated = [regex]::Replace(
        $manifest,
        'assemblyIdentity(\s+)version="[\d.]+"',
        "assemblyIdentity`${1}version=`"$four`"")
    if ($updated -eq $manifest) {
        throw "Failed to update assemblyIdentity version in app.manifest"
    }
    [System.IO.File]::WriteAllText($manifestPath, $updated, $Utf8NoBom)

    $wixPath = Join-Path $Root "installer\WinDNLA.Setup\WinDNLA.Setup.wixproj"
    $wix = [System.IO.File]::ReadAllText($wixPath)
    $wixUpdated = [regex]::Replace(
        $wix,
        '(<ProductVersion\b[^>]*>)[^<]+(</ProductVersion>)',
        "`${1}$display`${2}")
    if ($wixUpdated -eq $wix) {
        throw "Failed to update ProductVersion in WinDNLA.Setup.wixproj"
    }
    [System.IO.File]::WriteAllText($wixPath, $wixUpdated, $Utf8NoBom)
}

$current = Read-AppVersion
if ($SkipBump) {
    $version = $current
    Write-Host ("Building WinDLNA {0} (version unchanged)" -f $version)
} else {
    $version = Step-PatchVersion $current
    Write-Host ("Bumping WinDLNA {0} -> {1}" -f $current, $version)
    Write-AppVersion $version
}

$buildMsi = Join-Path $PSScriptRoot "build-msi.ps1"
& $buildMsi -Configuration $Configuration -Version $version
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
