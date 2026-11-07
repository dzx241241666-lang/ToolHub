param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$taskLocalSdk = Join-Path $env:LOCALAPPDATA 'CodexTools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $taskLocalSdk) { $taskDotnetPath = $taskLocalSdk }
elseif ($taskDotnet) { $taskDotnetPath = $taskDotnet.Source }
else { throw '.NET 8 SDK 未安装' }
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'tools\prepare-conversion.ps1')
    $taskRuntimeArguments = @()
    if ($SelfContained) { $taskRuntimeArguments = @('-p:RuntimeFrameworkVersion=8.0.33') }
    & $taskDotnetPath publish Switchboard.csproj -c Release -r win-x64 --self-contained ($SelfContained.IsPresent.ToString().ToLowerInvariant()) -o artifacts\ToolHub -p:PublishSingleFile=true -p:DebugType=None -p:IncludeNativeLibrariesForSelfExtract=true @taskRuntimeArguments
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    Copy-Item -LiteralPath README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination artifacts\ToolHub
    $taskPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $taskLicenses = Join-Path $PSScriptRoot 'artifacts\ToolHub\licenses'
    New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskPackages 'magick.net-q8-x64\15.1.0\Notice.txt') -Destination (Join-Path $taskLicenses 'Magick.NET-ImageMagick-NOTICE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\Apache-2.0.txt') -Destination $taskLicenses -Force
    if (Test-Path -LiteralPath artifacts\ToolHub-Send-Android.apk) { Copy-Item -LiteralPath artifacts\ToolHub-Send-Android.apk -Destination artifacts\ToolHub -Force }
    if ($SelfContained) {
        $taskPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $taskLicenses = Join-Path $PSScriptRoot 'artifacts\ToolHub\licenses'
        New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
        foreach ($taskFramework in @('microsoft.netcore.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
            foreach ($taskNotice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
                $taskSource = Join-Path $taskPackages "$taskFramework\8.0.33\$taskNotice"
                if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskLicenses "$taskFramework-$taskNotice") -Force }
            }
        }
    }
    Compress-Archive -Path artifacts\ToolHub\* -DestinationPath artifacts\ToolHub-win-x64.zip -Force
} finally { Pop-Location }
