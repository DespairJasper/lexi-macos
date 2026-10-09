param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath -Parent) | Out-Null
& $compiler /nologo /target:exe "/out:$OutputPath" (Join-Path $PSScriptRoot 'OptimizerProcessFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Windows test process compilation failed.' }
