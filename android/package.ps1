param([string]$Destination = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lexi Android 0.2.2 预览版'))
$ErrorActionPreference = 'Stop'
$apk = Join-Path $PSScriptRoot 'app/build/outputs/apk/debug/app-debug.apk'
if (!(Test-Path -LiteralPath $apk)) { throw '请先执行 gradlew.bat assembleDebug 生成 APK。' }
New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item -LiteralPath $apk -Destination (Join-Path $Destination 'Lexi-Android-0.2.2-alpha.apk') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $Destination '安装与使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DELIVERY.md') -Destination (Join-Path $Destination '交付范围与注意事项.md') -Force
Add-Type -AssemblyName System.IO.Compression
$output = [IO.File]::Create((Join-Path $Destination 'Lexi-Android-0.2.2-源码.zip'))
$zip = [IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot,$_.FullName)
        if ($relative -match '(^|[\\/])(build|\.gradle|\.git)([\\/])|(^|[\\/])local\.properties$|\.(jks|keystore)$|keystore\.properties$') { return }
        $entry = $zip.CreateEntry('lexi_android/' + $relative.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)
        $source = [IO.File]::OpenRead($_.FullName); $target = $entry.Open()
        try { $source.CopyTo($target) } finally { $source.Dispose(); $target.Dispose() }
    }
} finally { $zip.Dispose(); $output.Dispose() }
$hashes = Get-ChildItem -LiteralPath $Destination -File | Where-Object Name -ne 'SHA256SUMS.txt' | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) }
[IO.File]::WriteAllLines((Join-Path $Destination 'SHA256SUMS.txt'),$hashes)
Get-ChildItem -LiteralPath $Destination -File | Select-Object Name,Length
