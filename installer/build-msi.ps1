#Requires -Version 5.1
param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$PublishDir = Join-Path $Root "artifacts\publish"
$OutDir = Join-Path $Root "artifacts"
$SetupProj = Join-Path $Root "installer\WinDNLA.Setup\WinDNLA.Setup.wixproj"

function Read-StoredVersion {
    $path = Join-Path $Root "src\Directory.Build.props"
    if (-not (Test-Path $path)) { return "1.0.0.2" }
    $raw = [System.IO.File]::ReadAllText($path)
    $m = [regex]::Match($raw, "(?s)<Version>\s*([^<]+?)\s*</Version>")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return "1.0.0.2"
}

function Get-FourPartVersion([string]$version) {
    $parts = @($version.Trim() -split '\.' | Where-Object { $_ -ne '' })
    while ($parts.Count -lt 4) { $parts += "0" }
    if ($parts.Count -gt 4) { $parts = $parts[0..3] }
    return ($parts[0..3] -join '.')
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-StoredVersion
}

$FileVersion = Get-FourPartVersion $Version

New-Item -ItemType Directory -Force -Path $PublishDir, $OutDir | Out-Null

$ffmpeg = Join-Path $Root "tools\ffmpeg\ffmpeg.exe"
$ffprobe = Join-Path $Root "tools\ffmpeg\ffprobe.exe"
if (-not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) {
    Write-Warning "ffmpeg.exe / ffprobe.exe not found in tools\ffmpeg - MSI will not include them. Copy binaries before packaging for a complete install."
}

Write-Host ('Publishing WinDLNA {0} ({1}, win-x64, self-contained)...' -f $Version, $Configuration)
dotnet publish (Join-Path $Root "src\WinDNLA.App\WinDNLA.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishTrimmed=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$FileVersion `
    -p:FileVersion=$FileVersion `
    -p:InformationalVersion=$Version `
    -o $PublishDir
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Ensure ffmpeg folder in publish output
$ffmpegOut = Join-Path $PublishDir "ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
if (Test-Path $ffmpeg) { Copy-Item $ffmpeg $ffmpegOut -Force }
if (Test-Path $ffprobe) { Copy-Item $ffprobe $ffmpegOut -Force }

$pri = Join-Path $PublishDir "WinDLNA.pri"
$logo = Join-Path $PublishDir "Assets\logo.png"
$xbf = Join-Path $PublishDir "MainWindow.xbf"
if (-not (Test-Path $pri) -or -not (Test-Path $xbf) -or -not (Test-Path $logo)) {
    throw "Publish output is missing WinUI resources (WinDLNA.pri / *.xbf / Assets\logo.png). Installed app would fail to start."
}

Write-Host "Building MSI with WiX..."
# Do not pass -p:DefineConstants — MSBuild splits on ';' and drops ProductVersion.
# WinDNLA.Setup.wixproj already maps PublishDir / ProductVersion into DefineConstants.
dotnet build $SetupProj -c $Configuration `
    -p:PublishDir="$PublishDir" `
    -p:ProductVersion=$Version
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $Root "installer\WinDNLA.Setup\bin") -Recurse -Filter "WinDLNA.msi" | Select-Object -First 1
if (-not $built) {
    throw "WinDLNA.msi was not produced. Is WiX SDK available? Try: dotnet restore $SetupProj"
}

$dest = Join-Path $OutDir ("WinDLNA-{0}-x64.msi" -f $Version)
Copy-Item $built.FullName $dest -Force
$latest = Join-Path $OutDir "WinDLNA.msi"
Copy-Item $built.FullName $latest -Force
Write-Host "MSI: $dest"
Write-Host "MSI: $latest"
