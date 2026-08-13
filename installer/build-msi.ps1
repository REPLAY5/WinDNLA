#Requires -Version 5.1
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$PublishDir = Join-Path $Root "artifacts\publish"
$OutDir = Join-Path $Root "artifacts"
$SetupProj = Join-Path $Root "installer\WinDNLA.Setup\WinDNLA.Setup.wixproj"

New-Item -ItemType Directory -Force -Path $PublishDir, $OutDir | Out-Null

$ffmpeg = Join-Path $Root "tools\ffmpeg\ffmpeg.exe"
$ffprobe = Join-Path $Root "tools\ffmpeg\ffprobe.exe"
if (-not (Test-Path $ffmpeg) -or -not (Test-Path $ffprobe)) {
    Write-Warning "ffmpeg.exe / ffprobe.exe not found in tools\ffmpeg — MSI will not include them. Copy binaries before packaging for a complete install."
}

Write-Host "Publishing WinDNLA ($Configuration, win-x64, self-contained)..."
dotnet publish (Join-Path $Root "src\WinDNLA.App\WinDNLA.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishTrimmed=false `
    -o $PublishDir

# Ensure ffmpeg folder in publish output
$ffmpegOut = Join-Path $PublishDir "ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
if (Test-Path $ffmpeg) { Copy-Item $ffmpeg $ffmpegOut -Force }
if (Test-Path $ffprobe) { Copy-Item $ffprobe $ffmpegOut -Force }

Write-Host "Building MSI with WiX..."
dotnet build $SetupProj -c $Configuration `
    -p:PublishDir="$PublishDir" `
    -p:ProductVersion=$Version `
    -p:DefineConstants="PublishDir=$PublishDir;ProductVersion=$Version"

$built = Get-ChildItem (Join-Path $Root "installer\WinDNLA.Setup\bin") -Recurse -Filter "WinDNLA.msi" | Select-Object -First 1
if (-not $built) {
    throw "WinDNLA.msi was not produced. Is WiX SDK available? Try: dotnet restore $SetupProj"
}

$dest = Join-Path $OutDir "WinDNLA-$Version-x64.msi"
Copy-Item $built.FullName $dest -Force
Write-Host "MSI: $dest"
