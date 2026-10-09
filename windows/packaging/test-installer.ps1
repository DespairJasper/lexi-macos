param([string]$Nsis = 'C:\Program Files (x86)\NSIS\makensis.exe', [string]$Version = '1.2.3', [string]$TestDirectory = 'installer-acceptance-verified')
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path (Split-Path $project -Parent) -Parent
$testRoot = Join-Path (Join-Path $workspace '.work') $TestDirectory
$output = Join-Path $testRoot 'output'
$appDir = Join-Path $testRoot 'app'
$dataDir = Join-Path $testRoot 'data'
if (Test-Path -LiteralPath (Join-Path $testRoot 'result.txt')) { throw 'Use a fresh installer acceptance directory.' }
New-Item -ItemType Directory -Force -Path $output,$dataDir | Out-Null
$report = [Collections.Generic.List[string]]::new()
function Check($condition, $message) {
  if (!$condition) { throw $message }
  $report.Add('PASS ' + $message)
}
Push-Location $PSScriptRoot
try {
  & $Nsis /INPUTCHARSET UTF8 /V2 "/DVERSION=$Version" "/DTEST_ROOT=$testRoot" "/DOUTDIR=$output" installer.nsi
  if ($LASTEXITCODE -ne 0) { throw 'Isolated installer compilation failed' }
} finally { Pop-Location }
$setup = Join-Path $output "Lexi-$Version-x64-setup.exe"
$lock = [IO.File]::Open((Join-Path $dataDir 'install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
  $blocked = Start-Process -FilePath $setup -ArgumentList '/S' -WindowStyle Hidden -Wait -PassThru
  Check ($blocked.ExitCode -eq 2) 'held data lock prevents installation'
} finally { $lock.Dispose() }
Set-Content -LiteralPath (Join-Path $dataDir 'preserve.txt') -Value 'isolated-user-data'
foreach ($step in @('install','upgrade')) {
  $process = Start-Process -FilePath $setup -ArgumentList '/S' -WindowStyle Hidden -Wait -PassThru
  Check ($process.ExitCode -eq 0) "$step exits successfully"
  foreach($file in @('Lexi.exe','Lexi.dll','fsrs-optimizer.exe','Uninstall.exe')) {
    Check (Test-Path -LiteralPath (Join-Path $appDir $file)) "$step includes $file"
  }
  Check ((Get-Content -LiteralPath (Join-Path $dataDir 'preserve.txt') -Raw).Trim() -eq 'isolated-user-data') "$step preserves user data"
  if($step -eq 'install') { Set-Content -LiteralPath (Join-Path $appDir 'user-added.txt') -Value 'keep' }
}
$env:LEXI_DATA_DIR = Join-Path $testRoot 'runtime-data'
$runtime = Start-Process -FilePath (Join-Path $appDir 'Lexi.exe') -ArgumentList '--redesign-test' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $testRoot 'installed-runtime.log') -RedirectStandardError (Join-Path $testRoot 'installed-runtime-errors.log')
Check ($runtime.ExitCode -eq 0) 'installed self-contained app passes redesign and recovery tests'
$runtimeReport = Get-Content -LiteralPath (Join-Path $env:LEXI_DATA_DIR 'redesign-result.txt')
Check (($runtimeReport | Where-Object {$_ -like 'PASS *'}).Count -ge 24 -and !($runtimeReport | Where-Object {$_ -like 'FAIL *'})) 'installed runtime emits complete successful assertion report'
Check (Test-Path -LiteralPath (Join-Path $env:LEXI_DATA_DIR 'vocab.sqlite3')) 'installed app creates an isolated database'
$mainData = $env:LEXI_DATA_DIR
$env:LEXI_DATA_DIR = Join-Path $testRoot 'runtime-ui'
$smoke = Start-Process -FilePath (Join-Path $appDir 'Lexi.exe') -ArgumentList '--ui-smoke' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $testRoot 'installed-ui.log') -RedirectStandardError (Join-Path $testRoot 'installed-ui-errors.log')
Check ($smoke.ExitCode -eq 0) 'installed app passes actual Windows UI smoke tests'
Check (!(Get-Content (Join-Path $env:LEXI_DATA_DIR 'ui-smoke-result.txt') | Where-Object {$_ -like 'FAIL *'})) 'installed UI report contains no failures'
$env:LEXI_DATA_DIR = Join-Path $testRoot 'runtime-interaction'
$interaction = Start-Process -FilePath (Join-Path $appDir 'Lexi.exe') -ArgumentList '--interaction-test' -WindowStyle Hidden -Wait -PassThru
Check ($interaction.ExitCode -eq 0) 'installed app passes new study, shortcut, focus and IELTS interactions'
$interactionReport = Get-Content (Join-Path $env:LEXI_DATA_DIR 'interaction-result.txt')
Check (($interactionReport | Where-Object {$_ -like 'PASS *'}).Count -ge 70 -and !($interactionReport | Where-Object {$_ -like 'FAIL *'})) 'installed interaction report contains complete successful assertions'
$env:LEXI_DATA_DIR = Join-Path $testRoot 'runtime-improvement'
$improvement = Start-Process -FilePath (Join-Path $appDir 'Lexi.exe') -ArgumentList '--improvement-test' -WindowStyle Hidden -Wait -PassThru
Check ($improvement.ExitCode -eq 0) 'installed app passes 1.2.3 theme, spelling, quotes, copy and writing acceptance'
$improvementReport = Get-Content (Join-Path $env:LEXI_DATA_DIR 'improvement-test-result.txt')
Check (($improvementReport | Where-Object {$_ -like 'PASS *'}).Count -ge 180 -and !($improvementReport | Where-Object {$_ -like 'FAIL *'})) 'installed improvement report contains complete successful assertions'
foreach($case in @(@('visual123-test','visual123-result.txt',95),@('quick-test','quick-test-result.txt',40))) {
  $env:LEXI_DATA_DIR = Join-Path $testRoot ('runtime-'+$case[0])
  $verified = Start-Process -FilePath (Join-Path $appDir 'Lexi.exe') -ArgumentList ('--'+$case[0]) -WindowStyle Hidden -Wait -PassThru
  Check ($verified.ExitCode -eq 0) ('installed app passes '+$case[0])
  $lines=Get-Content (Join-Path $env:LEXI_DATA_DIR $case[1])
  Check (($lines | Where-Object {$_ -like 'PASS *'}).Count -ge $case[2] -and !($lines | Where-Object {$_ -match '^(FAIL|ERROR)'})) ('installed report verified: '+$case[0])
}
$uninstaller = Join-Path $appDir 'Uninstall.exe'
# NSIS treats all remaining text after _?= as the directory, including spaces.
# Quoting the entire token bypasses this special parser and spawns a child copy.
$process = Start-Process -FilePath $uninstaller -ArgumentList @('/S',('_?='+$appDir)) -WindowStyle Hidden -Wait -PassThru
Check ($process.ExitCode -eq 0) 'uninstall exits successfully'
Check (!(Test-Path -LiteralPath (Join-Path $appDir 'Lexi.exe'))) 'uninstall removes packaged executable'
Check (Test-Path -LiteralPath (Join-Path $appDir 'user-added.txt')) 'uninstall preserves user-added files'
Check (Test-Path -LiteralPath (Join-Path $dataDir 'preserve.txt')) 'uninstall preserves existing data'
Check (Test-Path -LiteralPath (Join-Path $env:LEXI_DATA_DIR 'vocab.sqlite3')) 'uninstall preserves runtime database'
Check (Test-Path -LiteralPath (Join-Path $mainData 'vocab.sqlite3')) 'uninstall preserves learning and recovery database'
$retainedHash = (Get-FileHash -LiteralPath (Join-Path $mainData 'vocab.sqlite3')).Hash
$reinstall = Start-Process -FilePath $setup -ArgumentList '/S' -WindowStyle Hidden -Wait -PassThru
Check ($reinstall.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $appDir 'Lexi.exe'))) 'reinstall succeeds'
Check ((Get-FileHash -LiteralPath (Join-Path $mainData 'vocab.sqlite3')).Hash -eq $retainedHash) 'reinstall preserves exact database bytes'
$cleanup = Start-Process -FilePath $uninstaller -ArgumentList @('/S',('_?='+$appDir)) -WindowStyle Hidden -Wait -PassThru
Check ($cleanup.ExitCode -eq 0 -and !(Test-Path -LiteralPath (Join-Path $appDir 'Lexi.exe'))) 'test application cleanup succeeds'
$report | Set-Content -LiteralPath (Join-Path $testRoot 'result.txt') -Encoding utf8
$report

