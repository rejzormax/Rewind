$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase,PresentationCore,PresentationFramework
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetRoot = Join-Path $projectRoot 'assets'
$privateVector = Join-Path $assetRoot 'rewind.svg'
if (Test-Path -LiteralPath $privateVector) {
    [xml]$svgDocument = Get-Content -LiteralPath $privateVector -Raw
    $pathData = $svgDocument.DocumentElement.FirstChild.GetAttribute('d')
} else {
    [xml]$fallback = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Rewind/Resources/LogoFallback.xaml') -Raw
    $pathData = $fallback.DocumentElement.LastChild.InnerText
    New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
    [IO.File]::WriteAllText($privateVector,'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 80"><path fill="white" d="' + $pathData + '"/></svg>')
    Write-Output 'Private branding is absent; generating a generic public-build icon.'
}
if (-not $pathData) { throw 'The Rewind vector mark is missing.' }
$geometry = [Windows.Media.Geometry]::Parse($pathData)

function Export-Logo([int]$size, [string]$filename, [bool]$transparent = $false, [bool]$rounded = $false) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    if (-not $transparent) {
        $brush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#111112'))
        $radius = if ($rounded) { $size * 0.18 } else { 0 }
        $drawing.DrawRoundedRectangle($brush, $null, [Windows.Rect]::new(0,0,$size,$size), $radius,$radius)
    }
    $bounds = $geometry.Bounds
    $scale = $size * 0.75 / $bounds.Width
    $transforms = [Windows.Media.TransformGroup]::new()
    $transforms.Children.Add([Windows.Media.ScaleTransform]::new($scale,$scale))
    $transforms.Children.Add([Windows.Media.TranslateTransform]::new(($size - $bounds.Width*$scale)/2-$bounds.X*$scale,($size-$bounds.Height*$scale)/2-$bounds.Y*$scale))
    $mark = $geometry.Clone()
    $mark.Transform = $transforms
    $drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,$mark)
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $assetRoot $filename))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

Export-Logo 512 'rewind.png' $false $true
Export-Logo 512 'rewind-transparent.png' $true $false
Export-Logo 1024 'rewind-preview.png' $false $false
Export-Logo 256 'rewind-icon.png' $true $false
$pngBytes = [IO.File]::ReadAllBytes((Join-Path $assetRoot 'rewind-icon.png'))
$stream = [IO.File]::Create((Join-Path $assetRoot 'rewind.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngBytes.Length); $writer.Write([uint32]22)
    $writer.Write($pngBytes)
} finally { $writer.Dispose() }
$resourceRoot = Join-Path $projectRoot 'src/Rewind/Resources'
New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
$dictionary = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Geometry x:Key="RewindLogoGeometry">' + $pathData + '</Geometry></ResourceDictionary>'
[IO.File]::WriteAllText((Join-Path $resourceRoot 'Logo.xaml'),$dictionary)
Write-Output 'Rendered the Rewind vector, app icon, transparent PNG and preview.'
