param(
  [string]$SourcePath = (Join-Path $PSScriptRoot 'branding\app-source.png'),
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\src\DusmpLauncher\UI\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$source = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $SourcePath).Path)
try {
  $frames = @()
  foreach ($size in @(16, 24, 32, 48, 64, 128, 256, 512)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
      $graphics.Clear([Drawing.Color]::Transparent)
      $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
      $margin = [math]::Max(1, [math]::Round($size * 0.035))
      $scale = [math]::Min(($size - 2 * $margin) / $source.Width, ($size - 2 * $margin) / $source.Height)
      $width = [int][math]::Round($source.Width * $scale)
      $height = [int][math]::Round($source.Height * $scale)
      $rect = [Drawing.Rectangle]::new([int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
      $graphics.DrawImage($source, $rect)
      if ($size -eq 512) {
        $bitmap.Save((Join-Path $OutputDirectory 'icon.png'), [Drawing.Imaging.ImageFormat]::Png)
      } else {
        $memory = [IO.MemoryStream]::new()
        try {
          $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
          $frames += [pscustomobject]@{ Size = $size; Data = $memory.ToArray() }
        } finally { $memory.Dispose() }
      }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
  }

  $stream = [IO.File]::Create((Join-Path $OutputDirectory 'app.ico'))
  $writer = [IO.BinaryWriter]::new($stream)
  try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = [uint32](6 + 16 * $frames.Count)
    foreach ($frame in $frames) {
      $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
      $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
      $writer.Write([byte]0); $writer.Write([byte]0)
      $writer.Write([uint16]1); $writer.Write([uint16]32)
      $writer.Write([uint32]$frame.Data.Length); $writer.Write($offset)
      $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
  } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $source.Dispose() }
