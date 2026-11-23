param([Parameter(Mandatory=$true)][string]$SourcePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SandUp.Desktop\Assets'
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null
Copy-Item -LiteralPath $SourcePath -Destination (Join-Path $assetRoot 'app-icon.png')
$source=[System.Drawing.Image]::FromFile((Join-Path $assetRoot 'app-icon.png'))
$frames=@()
try {
    foreach($size in @(16,24,32,48,64,128,256)){
        $bitmap=[System.Drawing.Bitmap]::new($size,$size)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        $stream=[System.IO.MemoryStream]::new()
        try {
            $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,0,0,$size,$size)
            $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
            $frames+=@{Size=$size;Bytes=$stream.ToArray()}
        } finally {$graphics.Dispose();$bitmap.Dispose();$stream.Dispose()}
    }
} finally {$source.Dispose()}
$output=[System.IO.File]::Create((Join-Path $assetRoot 'app.ico'))
$writer=[System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
    $offset=6+16*$frames.Count
    foreach($frame in $frames){
        $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
        $offset+=$frame.Bytes.Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
} finally {$writer.Dispose()}
Write-Output '已保留原图，并转换为7种尺寸的Windows图标。'
