# Requires PowerShell 7, .NET 8 SDK and NSIS 3 Unicode.
param(
  [string]$Nsis = 'C:\Program Files (x86)\NSIS\makensis.exe',
  [string]$Dotnet = '',
  [string]$Version = '1.2.4',
  [string]$OutputDir = '',
  [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'

# Project and dynamic roots
$project = Split-Path $PSScriptRoot -Parent
$versionRoot = Split-Path $project -Parent
$workspaceRoot = Split-Path $versionRoot -Parent

# Resolve dotnet executable: parameter > workspace .tools > system PATH
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
  $candidateDotnet = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
  if (Test-Path -LiteralPath $candidateDotnet) {
    $Dotnet = $candidateDotnet
  } else {
    $Dotnet = 'dotnet'
  }
}

# Resolve release directory (default: workspace output dir under Lexi 1.2.4 Windows)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
  $release = Join-Path $versionRoot "Lexi-$Version-Windows-x64"
} else {
  $release = $OutputDir
}

$publish = Join-Path $project 'publish'

# Check FSRS helper requirement before publish
$fsrsHelper = Join-Path $project 'native\fsrs-optimizer\target\release\fsrs-optimizer.exe'
if (!(Test-Path -LiteralPath $fsrsHelper)) {
  throw "Missing fsrs-optimizer.exe helper at '$fsrsHelper'. Publish must include fsrs-optimizer.exe."
}

if (!$SkipPublish) {
  & $Dotnet publish (Join-Path $project 'Lexi.csproj') -c Release -r win-x64 --self-contained true -o $publish
  if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

# Verify publish outputs
if (!(Test-Path -LiteralPath (Join-Path $publish 'Lexi.exe'))) {
  throw 'Publish output verification failed: Lexi.exe is missing. Run publish first.'
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'fsrs-optimizer.exe'))) {
  throw 'Publish output verification failed: fsrs-optimizer.exe is missing. Publish must include fsrs-optimizer.exe.'
}

if (!(Test-Path -LiteralPath $Nsis)) {
  throw "NSIS compiler not found at '$Nsis'. Specify the NSIS compiler with -Nsis."
}

New-Item -ItemType Directory -Force -Path $release | Out-Null

$lines = [Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object Extension -ne '.pdb' | ForEach-Object {
  $lines.Add('Delete "$INSTDIR\' + [IO.Path]::GetRelativePath($publish, $_.FullName) + '"')
}
Get-ChildItem -LiteralPath $publish -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
  $lines.Add('RMDir "$INSTDIR\' + [IO.Path]::GetRelativePath($publish, $_.FullName) + '"')
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'lexi-uninstall-files.nsh'), $lines, [Text.UTF8Encoding]::new($true))

Push-Location $PSScriptRoot
try {
  & $Nsis /INPUTCHARSET UTF8 /V2 /DVERSION="$Version" /DOUTDIR="$release" installer.nsi
  if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
} finally {
  Pop-Location
}

