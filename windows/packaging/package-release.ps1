param(
  [string]$Version = '1.2.4',
  [string]$OutputDir = '',
  [switch]$CopyToDesktop
)
$ErrorActionPreference = 'Stop'

# Project and dynamic roots
$project = Split-Path $PSScriptRoot -Parent
$versionRoot = Split-Path $project -Parent
$workspaceRoot = Split-Path $versionRoot -Parent

# Release directory containing installer and standalone files
$release = Join-Path $versionRoot "Lexi-$Version-Windows-x64"
$installer = Join-Path $release "Lexi-$Version-x64-setup.exe"
if (!(Test-Path -LiteralPath $installer)) { throw "Installer not found at '$installer'. Build the installer first." }

# Verify publish has fsrs-optimizer.exe as well
$publish = Join-Path $project 'publish'
if (Test-Path -LiteralPath $publish) {
  $fsrsPublish = Join-Path $publish 'fsrs-optimizer.exe'
  if (!(Test-Path -LiteralPath $fsrsPublish)) {
    throw "Missing fsrs-optimizer.exe in publish directory ('$fsrsPublish'). Packaging cannot proceed without fsrs-optimizer.exe."
  }
}

# Copy documentation and notices into release folder
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination (Join-Path $release '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'RELEASE-NOTES.md') -Destination (Join-Path $release '版本说明.md') -Force
if (Test-Path -LiteralPath (Join-Path $project 'ACCEPTANCE.md')) {
  Copy-Item -LiteralPath (Join-Path $project 'ACCEPTANCE.md') -Destination (Join-Path $release '验收说明.md') -Force
}
if (Test-Path -LiteralPath (Join-Path $project 'Notices')) {
  Copy-Item -LiteralPath (Join-Path $project 'Notices') -Destination (Join-Path $release '第三方许可') -Recurse -Force
}

# Create source code zip
$sourceZip = Join-Path $versionRoot "Lexi-$Version-Avalonia-源码.zip"
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Create($sourceZip)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem -LiteralPath $project -Recurse -File | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($project, $_.FullName)
    $bundledHelper = $relative.Replace('\','/') -eq 'native/fsrs-optimizer/target/release/fsrs-optimizer.exe'
    if (!$bundledHelper -and $relative -match '(^|[\\/])(bin|obj|publish|work|\.work|target|\.git|\.avalonia-build-tasks)([\\/])') { return }
    if ($relative -match '(vocab\.sqlite3|\.dpapi|\.env$|appsettings\.local\.json)') { throw "Unexpected private file: $relative" }
    $entry = $zip.CreateEntry('lexi_avalonia/' + $relative.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
    $entryStream = $entry.Open()
    $inputStream = [IO.File]::OpenRead($_.FullName)
    try { $inputStream.CopyTo($entryStream) } finally { $inputStream.Dispose(); $entryStream.Dispose() }
  }
} finally {
  $zip.Dispose()
  $stream.Dispose()
}

# Compute hashes inside release folder
$hashes = Get-ChildItem -LiteralPath $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) }
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($true))

# Create full distribution zip
$distributionZip = Join-Path $versionRoot "Lexi-$Version-Windows-x64-完整分发包.zip"
Compress-Archive -LiteralPath $release -DestinationPath $distributionZip -Force

# Workspace target output directory (default: $versionRoot inside workspace)
$targetDir = if ([string]::IsNullOrWhiteSpace($OutputDir)) { $versionRoot } else { $OutputDir }
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# Copy standalone setup exe alongside zip packages in workspace output directory
$targetInstaller = Join-Path $targetDir "Lexi-$Version-x64-setup.exe"
if ($installer -ne $targetInstaller) {
  Copy-Item -LiteralPath $installer -Destination $targetInstaller -Force
}

# Compute SHA256 checksums for workspace distribution files
$workspacePackageFiles = @(
  $targetInstaller,
  $sourceZip,
  $distributionZip
) | Where-Object { Test-Path -LiteralPath $_ }

$packageHashes = $workspacePackageFiles | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) }
[IO.File]::WriteAllLines((Join-Path $targetDir 'SHA256SUMS.txt'), $packageHashes, [Text.UTF8Encoding]::new($true))

# Optional desktop copy only if explicitly requested
if ($CopyToDesktop) {
  $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "Lexi $Version Windows"
  New-Item -ItemType Directory -Force $desktop | Out-Null
  Copy-Item -LiteralPath $sourceZip,$distributionZip,$installer -Destination $desktop -Force
  [IO.File]::WriteAllLines((Join-Path $desktop 'SHA256SUMS.txt'), $packageHashes, [Text.UTF8Encoding]::new($true))
}

Get-ChildItem -LiteralPath $targetDir -File | Where-Object { $_.Name -match "^(Lexi-$Version|SHA256SUMS\.txt)" } | Select-Object FullName, Length

