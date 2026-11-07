$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDestination = Join-Path $taskRoot '.tools\ffmpeg'
if ((Test-Path -LiteralPath (Join-Path $taskDestination 'ffmpeg.exe')) -and (Test-Path -LiteralPath (Join-Path $taskDestination 'ffprobe.exe'))) { return }
$taskCache = Join-Path $taskRoot '.tools\conversion-cache'
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
$taskZip = Join-Path $taskCache 'ffmpeg-9.0.2.zip'
$taskHash = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
if (!(Test-Path -LiteralPath $taskZip)) { Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' -OutFile $taskZip }
if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash -ne $taskHash) { throw 'FFmpeg 校验失败' }
$taskExpanded = Join-Path $taskCache 'ffmpeg-9.0.2-essentials_build'
if (!(Test-Path -LiteralPath (Join-Path $taskExpanded 'bin\ffmpeg.exe'))) { Expand-Archive -LiteralPath $taskZip -DestinationPath $taskCache -Force }
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskExpanded 'bin\ffmpeg.exe'),(Join-Path $taskExpanded 'bin\ffprobe.exe'),(Join-Path $taskExpanded 'LICENSE'),(Join-Path $taskExpanded 'README.txt') -Destination $taskDestination -Force
