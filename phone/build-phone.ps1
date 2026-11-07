param(
    [string]$ToolRoot = (Join-Path $env:LOCALAPPDATA 'CodexTools\android-build'),
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$Keystore,
    [string]$KeyAlias = 'switchboard-send'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $ToolRoot ('build-work\' + [Guid]::NewGuid().ToString('N'))
$taskJdk = if ($JavaHome) { Join-Path $JavaHome 'bin' } else { Join-Path $ToolRoot 'jdk\jdk-17.0.21+9\bin' }
if (!$AndroidSdk) { $AndroidSdk = $env:ANDROID_SDK_ROOT }
$taskTools = if ($AndroidSdk) { Join-Path $AndroidSdk 'build-tools\35.0.0' } else { Join-Path $ToolRoot 'build-tools\android-15' }
$taskPlatform = if ($AndroidSdk) { Join-Path $AndroidSdk 'platforms\android-35\android.jar' } else { Join-Path $ToolRoot 'platforms\android-35-ext15\android.jar' }
foreach ($taskRequired in @("$taskJdk\javac.exe", "$taskTools\aapt2.exe", $taskPlatform)) { if (!(Test-Path -LiteralPath $taskRequired)) { throw "缺少构建工具：$taskRequired" } }
New-Item -ItemType Directory -Force -Path $taskOutput,"$taskOutput\generated","$taskOutput\classes","$taskOutput\dex","$taskOutput\assets" | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot\res","$PSScriptRoot\src","$PSScriptRoot\AndroidManifest.xml" -Destination $taskOutput -Recurse
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination "$taskOutput\assets\LICENSE" -Force
function Invoke-Checked([string]$taskExe,[string[]]$taskArgs) { & $taskExe @taskArgs; if ($LASTEXITCODE -ne 0) { throw "构建失败：$taskExe" } }
Invoke-Checked "$taskTools\aapt2.exe" @('compile','--dir',"$taskOutput\res",'-o',"$taskOutput\resources.zip")
Invoke-Checked "$taskTools\aapt2.exe" @('link','-o',"$taskOutput\resources.apk",'-I',$taskPlatform,'--manifest',"$taskOutput\AndroidManifest.xml",'--java',"$taskOutput\generated",'-A',"$taskOutput\assets",'--min-sdk-version','29','--target-sdk-version','35','--version-code','9','--version-name','0.5.0',"$taskOutput\resources.zip")
$taskSources = @(Get-ChildItem -LiteralPath "$taskOutput\src","$taskOutput\generated" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
Invoke-Checked "$taskJdk\javac.exe" (@('-source','8','-target','8','-encoding','UTF-8','-bootclasspath',"$taskPlatform;$taskTools\core-lambda-stubs.jar",'-d',"$taskOutput\classes") + $taskSources)
Invoke-Checked "$taskJdk\jar.exe" @('cf',"$taskOutput\classes.jar",'-C',"$taskOutput\classes",'.')
Invoke-Checked "$taskJdk\java.exe" @('-cp',"$taskTools\lib\d8.jar",'com.android.tools.r8.D8','--lib',$taskPlatform,'--min-api','29','--output',"$taskOutput\dex", "$taskOutput\classes.jar")
Copy-Item -LiteralPath "$taskOutput\resources.apk" -Destination "$taskOutput\unsigned.apk" -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::Open("$taskOutput\unsigned.apk",[IO.Compression.ZipArchiveMode]::Update)
try { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,"$taskOutput\dex\classes.dex",'classes.dex',[IO.Compression.CompressionLevel]::Optimal) | Out-Null } finally { $taskArchive.Dispose() }
Invoke-Checked "$taskTools\zipalign.exe" @('-f','4',"$taskOutput\unsigned.apk","$taskOutput\aligned.apk")
$taskKey = if ($Keystore) { [IO.Path]::GetFullPath($Keystore) } else { Join-Path $ToolRoot 'switchboard-send.jks' }
if (!$env:TOOLHUB_KEYSTORE_PASSWORD) { $env:TOOLHUB_KEYSTORE_PASSWORD = 'android' }
if (!(Test-Path -LiteralPath $taskKey)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskKey) -Force | Out-Null
    Invoke-Checked "$taskJdk\keytool.exe" @('-genkeypair','-keystore',$taskKey,'-alias',$KeyAlias,'-storepass:env','TOOLHUB_KEYSTORE_PASSWORD','-keypass:env','TOOLHUB_KEYSTORE_PASSWORD','-keyalg','RSA','-keysize','3072','-validity','10000','-dname','CN=ToolHub Send','-noprompt')
}
$taskApk = Join-Path $taskRoot 'artifacts\ToolHub-Send-Android.apk'
Invoke-Checked "$taskJdk\java.exe" @('-jar',"$taskTools\lib\apksigner.jar",'sign','--ks',$taskKey,'--ks-key-alias',$KeyAlias,'--ks-pass','env:TOOLHUB_KEYSTORE_PASSWORD','--key-pass','env:TOOLHUB_KEYSTORE_PASSWORD','--out',"$taskOutput\signed.apk","$taskOutput\aligned.apk")
Invoke-Checked "$taskJdk\java.exe" @('-jar',"$taskTools\lib\apksigner.jar",'verify','--verbose',"$taskOutput\signed.apk")
Invoke-Checked "$taskTools\aapt2.exe" @('dump','badging',"$taskOutput\signed.apk")
Copy-Item -LiteralPath "$taskOutput\signed.apk" -Destination $taskApk -Force
Write-Output "Android APK: $taskApk"
