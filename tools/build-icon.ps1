param([string]$Source = (Join-Path $PSScriptRoot '..\Assets\ToolHub.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssetRoot = Join-Path $PSScriptRoot '..\Assets'
$taskImage = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($Source))
$taskFrames = [Collections.Generic.List[object]]::new()
try {
    foreach ($taskSize in @(16,20,24,32,48,64,128,192,256)) {
        $taskBitmap = [Drawing.Bitmap]::new($taskSize,$taskSize,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
        $taskStream = [IO.MemoryStream]::new()
        try {
            $taskGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskGraphics.DrawImage($taskImage,[Drawing.Rectangle]::new(0,0,$taskSize,$taskSize))
            $taskBitmap.Save($taskStream,[Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add([pscustomobject]@{Size=$taskSize; Bytes=$taskStream.ToArray()})
            if ($taskSize -eq 256) {
                $taskPhoneDirectory = Join-Path $PSScriptRoot '..\phone\res\drawable-nodpi'
                New-Item -ItemType Directory -Path $taskPhoneDirectory -Force | Out-Null
                [IO.File]::WriteAllBytes((Join-Path $taskPhoneDirectory 'ic_toolhub.png'),$taskStream.ToArray())
            }
        } finally { $taskStream.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
    }
} finally { $taskImage.Dispose() }
$taskOutput = [IO.File]::Create((Join-Path $taskAssetRoot 'ToolHub.ico'))
$taskWriter = [IO.BinaryWriter]::new($taskOutput)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrame.Bytes.Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrame.Bytes.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Bytes) }
} finally { $taskWriter.Dispose() }
Write-Output 'ToolHub icon: 9 resolutions, Android 256px'
