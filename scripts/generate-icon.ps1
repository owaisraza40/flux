$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sfIconRoot = Split-Path -Parent $PSScriptRoot
$sfAssetDirectory = Join-Path $sfIconRoot 'assets'
New-Item -ItemType Directory -Path $sfAssetDirectory -Force | Out-Null
$sfIconBitmap = [System.Drawing.Bitmap]::new(256,256)
$sfIconGraphics = [System.Drawing.Graphics]::FromImage($sfIconBitmap)
$sfIconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$sfIconGraphics.Clear([System.Drawing.Color]::FromArgb(10,10,12))
$sfIconBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(200,16,46))
$sfIconPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(200,16,46),18)
$sfIconPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$sfIconPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$sfIconGraphics.DrawLine($sfIconPen,128,52,128,151)
$sfIconGraphics.DrawLines($sfIconPen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(84,115),[System.Drawing.Point]::new(128,159),[System.Drawing.Point]::new(172,115)))
$sfIconGraphics.DrawLines($sfIconPen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(66,170),[System.Drawing.Point]::new(66,194),[System.Drawing.Point]::new(190,194),[System.Drawing.Point]::new(190,170)))
$sfPngStream = [System.IO.MemoryStream]::new()
$sfIconBitmap.Save($sfPngStream,[System.Drawing.Imaging.ImageFormat]::Png)
$sfPngBytes = $sfPngStream.ToArray()
$sfIcoStream = [System.IO.File]::Create((Join-Path $sfAssetDirectory 'flux.ico'))
$sfIcoWriter = [System.IO.BinaryWriter]::new($sfIcoStream)
$sfIcoWriter.Write([uint16]0); $sfIcoWriter.Write([uint16]1); $sfIcoWriter.Write([uint16]1)
$sfIcoWriter.Write([byte]0); $sfIcoWriter.Write([byte]0); $sfIcoWriter.Write([byte]0); $sfIcoWriter.Write([byte]0)
$sfIcoWriter.Write([uint16]1); $sfIcoWriter.Write([uint16]32); $sfIcoWriter.Write([uint32]$sfPngBytes.Length); $sfIcoWriter.Write([uint32]22)
$sfIcoWriter.Write($sfPngBytes); $sfIcoWriter.Dispose(); $sfPngStream.Dispose(); $sfIconGraphics.Dispose(); $sfIconBitmap.Dispose(); $sfIconPen.Dispose(); $sfIconBrush.Dispose()
