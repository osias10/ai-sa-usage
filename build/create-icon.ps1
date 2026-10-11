param()
$ErrorActionPreference = 'Stop'

# Rebuild the app icon from vector geometry. No fonts, downloaded artwork, or CLI tools are required.
# Keep the 32-unit drawing in sync with TrayService.CreateIcon.
Add-Type -AssemblyName System.Drawing
if (-not ('AisaIconDrawing' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class AisaIconDrawing
{
    public static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(66, 99, 235)))
        using (var tile = new GraphicsPath())
        using (var ink = new Pen(Color.White, 2.2f))
        using (var letterS = new GraphicsPath())
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(size / 32f, size / 32f);
            tile.AddArc(1, 1, 13, 13, 180, 90);
            tile.AddArc(18, 1, 13, 13, 270, 90);
            tile.AddArc(18, 18, 13, 13, 0, 90);
            tile.AddArc(1, 18, 13, 13, 90, 90);
            tile.CloseFigure();
            graphics.FillPath(background, tile);
            ink.StartCap = LineCap.Round;
            ink.EndCap = LineCap.Round;
            ink.LineJoin = LineJoin.Round;
            letterS.AddBezier(14.1f, 11.5f, 12.8f, 9.8f, 7.7f, 10, 7.7f, 12.8f);
            letterS.AddBezier(7.7f, 12.8f, 7.7f, 15.6f, 14.5f, 14.4f, 14.5f, 18.3f);
            letterS.AddBezier(14.5f, 18.3f, 14.5f, 21.5f, 9.5f, 22.2f, 7.6f, 20.4f);
            graphics.DrawPath(ink, letterS);
            graphics.DrawLines(ink, new[] { new PointF(17.3f, 21), new PointF(21.3f, 10.7f), new PointF(25.3f, 21) });
            graphics.DrawLine(ink, 18.7f, 17.5f, 23.9f, 17.5f);
        }
        return bitmap;
    }
}
'@
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $repoRoot 'src\AIUsage.App\Assets'
$previewDirectory = Join-Path $repoRoot '.artifacts\branding'
[IO.Directory]::CreateDirectory($assetsDirectory) | Out-Null
[IO.Directory]::CreateDirectory($previewDirectory) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [AisaIconDrawing]::Draw($size)
    $png = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
        $images.Add($png.ToArray())
        if ($size -eq 256) {
            $bitmap.Save((Join-Path $previewDirectory 'AISA.png'), [Drawing.Imaging.ImageFormat]::Png)
        }
    } finally {
        $png.Dispose()
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $assetsDirectory 'AISA.ico'
$stream = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = $sizes[$index] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
} finally {
    $writer.Dispose()
    $stream.Dispose()
}
Write-Output "Created $iconPath (16, 24, 32, 48, 64, 128, 256 px)."
