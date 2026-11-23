# Build the mod: dotnet build -c Release -> dist\Pathologic2WorldTuner.dll
# and a release archive dist\Pathologic2WorldTuner-<version>.zip that is extracted into the game folder
# (it contains BepInEx\plugins\pathologic2-world-tuner\...).
# Example: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-GameDir "D:\Games\Pathologic"]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Pathologic"
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$name = 'Pathologic2WorldTuner'

if (-not (Test-Path "$GameDir\Pathologic_Data\Managed\Assembly-CSharp.dll")) { throw "Game not found: $GameDir" }
if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.dll")) { throw "BepInEx not found in the game folder: $GameDir\BepInEx\core" }

& dotnet build "$root\$name.csproj" -c Release -nologo "-p:GameDir=$GameDir" "-p:OutDir=$root\bin\Release\"
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\bin\Release\$name.dll" $dist -Force
$f = Get-Item "$dist\$name.dll"
"Done: $($f.FullName) ($($f.Length) bytes)"

# Release archive
$version = $f.VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
# ZipArchive instead of Compress-Archive: PS 5.1 writes entry names with backslashes, which some unpackers mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$prefix = 'BepInEx/plugins/pathologic2-world-tuner/'
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @($f.FullName, "$root\README.md", "$root\LICENSE")) {
        $entry = $prefix + [IO.Path]::GetFileName($file)
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entry) | Out-Null
    }
} finally {
    $archive.Dispose()
}
"Release archive: $zip"
