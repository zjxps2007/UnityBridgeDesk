$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskSource = Join-Path $deskRoot 'design/branding/desk-icon.xaml'
$deskArtwork = [System.Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($deskSource))
$deskFrames = @()
foreach ($deskSize in @(16,20,24,32,40,48,64,128,256)) {
    $deskVisual = [System.Windows.Media.DrawingVisual]::new()
    $deskContext = $deskVisual.RenderOpen()
    $deskContext.PushTransform([System.Windows.Media.ScaleTransform]::new($deskSize/128.0,$deskSize/128.0))
    $deskContext.DrawDrawing($deskArtwork.Drawing)
    $deskContext.Pop(); $deskContext.Close()
    $deskBitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($deskSize,$deskSize,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $deskBitmap.Render($deskVisual)
    $deskEncoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $deskEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($deskBitmap))
    $deskStream = [IO.MemoryStream]::new(); $deskEncoder.Save($deskStream)
    $deskFrames += @{size=$deskSize;bytes=$deskStream.ToArray()}; $deskStream.Dispose()
}
$deskOutput = Join-Path $deskRoot 'src/UnityBridgeDesk.Desktop/Assets/desk.ico'
$deskFile = [IO.File]::Create($deskOutput); $deskWriter = [IO.BinaryWriter]::new($deskFile)
try {
    $deskWriter.Write([uint16]0); $deskWriter.Write([uint16]1); $deskWriter.Write([uint16]$deskFrames.Count)
    $deskOffset = 6 + 16*$deskFrames.Count
    foreach ($deskFrame in $deskFrames) {
        $deskDimension = if($deskFrame.size -eq 256){0}else{$deskFrame.size}
        $deskWriter.Write([byte]$deskDimension); $deskWriter.Write([byte]$deskDimension)
        $deskWriter.Write([byte]0); $deskWriter.Write([byte]0); $deskWriter.Write([uint16]1); $deskWriter.Write([uint16]32)
        $deskWriter.Write([uint32]$deskFrame.bytes.Length); $deskWriter.Write([uint32]$deskOffset)
        $deskOffset += $deskFrame.bytes.Length
    }
    foreach($deskFrame in $deskFrames){$deskWriter.Write([byte[]]$deskFrame.bytes)}
}
finally {$deskWriter.Dispose();$deskFile.Dispose()}
[IO.File]::WriteAllBytes((Join-Path $deskRoot 'design/branding/desk-icon.png'),$deskFrames[-1].bytes)
Write-Output "Created app icon: 16/20/24/32/40/48/64/128/256 px from the vector mascot."
