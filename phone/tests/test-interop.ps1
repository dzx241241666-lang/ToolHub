param([string]$ToolRoot = (Join-Path $env:LOCALAPPDATA 'CodexTools\android-build'))
$ErrorActionPreference='Stop'
$taskRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskJdk = Join-Path $ToolRoot 'jdk\jdk-17.0.21+9\bin'
$taskJson = Join-Path $ToolRoot 'json-20240303.jar'
if (!(Test-Path -LiteralPath $taskJson)) { Invoke-WebRequest 'https://repo.maven.apache.org/maven2/org/json/json/20240303/json-20240303.jar' -OutFile $taskJson }
$taskWork = Join-Path $taskRoot ('artifacts\interop-' + [Guid]::NewGuid().ToString('N'))
$taskCompile = Join-Path $ToolRoot ('interop-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path "$taskWork\source",$taskCompile | Out-Null
[IO.File]::WriteAllText("$taskWork\source\样例.txt","Android / Windows 互传校验"+[Environment]::NewLine,[Text.Encoding]::UTF8)
$taskBytes = New-Object byte[] 350000
[Security.Cryptography.RandomNumberGenerator]::Fill($taskBytes)
[IO.File]::WriteAllBytes("$taskWork\source\binary.bin",$taskBytes)
[IO.File]::WriteAllBytes("$taskWork\source\empty.bin",[byte[]]@())
& "$taskJdk\keytool.exe" -genkeypair -keystore "$taskCompile\identity.p12" -storetype PKCS12 -alias interop -storepass test-only -keypass test-only -keyalg EC -groupname secp256r1 -validity 2 -dname 'CN=interop-loopback' -noprompt
if ($LASTEXITCODE -ne 0) { throw '测试证书生成失败' }
& "$taskJdk\javac.exe" -encoding UTF-8 -cp $taskJson -d $taskCompile "$taskRoot\phone\src\dev\switchboard\send\PeerEngine.java" "$taskRoot\phone\src\dev\switchboard\send\PayloadArchive.java" "$PSScriptRoot\Interop.java"
if ($LASTEXITCODE -ne 0) { throw '测试核心编译失败' }
$taskJavaArgs = @('-cp',"$taskCompile;$taskJson",'dev.switchboard.send.Interop',"$taskCompile\identity.p12",$taskWork)
$taskStart = New-Object Diagnostics.ProcessStartInfo
$taskStart.FileName="$taskJdk\java.exe"; $taskStart.UseShellExecute=$false; $taskStart.CreateNoWindow=$true
foreach ($taskArgument in $taskJavaArgs) { $taskStart.ArgumentList.Add($taskArgument) }
$taskJava = [Diagnostics.Process]::Start($taskStart)
try {
    for ($taskWait=0; $taskWait -lt 100 -and !(Test-Path -LiteralPath "$taskWork\java-ready.json"); $taskWait++) { Start-Sleep -Milliseconds 100; if ($taskJava.HasExited) { throw 'Java 测试服务提前退出' } }
    $taskExe = Join-Path $taskRoot 'bin\Release\net8.0-windows\ToolHub.exe'
    $taskPc = Start-Process -FilePath $taskExe -ArgumentList @('--transfer-interop',('"'+$taskWork+'"')) -WindowStyle Hidden -PassThru -Wait
    if ($taskPc.ExitCode -ne 0) { Get-Content "$taskWork\pc-error.txt"; throw '电脑端兼容测试失败' }
    if (!$taskJava.WaitForExit(10000) -or $taskJava.ExitCode -ne 0) { throw 'Java 兼容测试失败' }
    Get-Content "$taskWork\interop-results.json"
    Copy-Item -LiteralPath "$taskWork\interop-results.json" -Destination "$taskRoot\artifacts\interop-results.json" -Force
} finally { if (!$taskJava.HasExited) { $taskJava.Kill() }; $taskJava.Dispose() }
