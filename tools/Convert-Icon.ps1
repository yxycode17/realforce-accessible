# Convert the finished PNG asset to a multi-resolution Windows ICO.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$rfRoot = Split-Path $PSScriptRoot -Parent
$rfSource = [Drawing.Image]::FromFile((Join-Path $rfRoot 'assets\assistant.png'))
$rfFrames = @()
try {
    foreach ($rfSize in @(16,24,32,48,64,128,256)) {
        $rfBitmap = New-Object Drawing.Bitmap($rfSize,$rfSize)
        $rfGraphics = [Drawing.Graphics]::FromImage($rfBitmap)
        $rfStream = New-Object IO.MemoryStream
        try {
            $rfGraphics.Clear([Drawing.Color]::Transparent)
            $rfGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $rfGraphics.DrawImage($rfSource,0,0,$rfSize,$rfSize)
            $rfBitmap.Save($rfStream,[Drawing.Imaging.ImageFormat]::Png)
            $rfFrames += ,@{Size=$rfSize;Bytes=$rfStream.ToArray()}
        } finally { $rfStream.Dispose();$rfGraphics.Dispose();$rfBitmap.Dispose() }
    }
    $rfFile = [IO.File]::Create((Join-Path $rfRoot 'assets\assistant.ico'))
    $rfWriter = New-Object IO.BinaryWriter($rfFile)
    try {
        $rfWriter.Write([uint16]0);$rfWriter.Write([uint16]1);$rfWriter.Write([uint16]$rfFrames.Count)
        $rfOffset = 6 + 16 * $rfFrames.Count
        foreach ($rfFrame in $rfFrames) {
            $rfDimension = if($rfFrame.Size -eq 256){0}else{$rfFrame.Size}
            $rfWriter.Write([byte]$rfDimension);$rfWriter.Write([byte]$rfDimension)
            $rfWriter.Write([byte]0);$rfWriter.Write([byte]0)
            $rfWriter.Write([uint16]1);$rfWriter.Write([uint16]32)
            $rfWriter.Write([uint32]$rfFrame.Bytes.Length);$rfWriter.Write([uint32]$rfOffset)
            $rfOffset += $rfFrame.Bytes.Length
        }
        foreach ($rfFrame in $rfFrames) { $rfWriter.Write([byte[]]$rfFrame.Bytes) }
    } finally { $rfWriter.Dispose();$rfFile.Dispose() }
} finally { $rfSource.Dispose() }
