param([string]$DataDirectory)

$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskExecutable = Join-Path $deskRoot 'src/UnityBridgeDesk.Desktop/bin/Release/net10.0-windows/UnityBridgeDesk.exe'
if (-not (Test-Path -LiteralPath $deskExecutable)) { throw '먼저 build/Verify.ps1 -Restore로 빌드해 주세요.' }
$deskStart = [System.Diagnostics.ProcessStartInfo]::new($deskExecutable)
$deskStart.UseShellExecute = $false
if ($DataDirectory) {
    if (-not [System.IO.Path]::IsPathFullyQualified($DataDirectory)) { throw 'DataDirectory는 절대 경로여야 합니다.' }
    $deskStart.ArgumentList.Add('--data-dir')
    $deskStart.ArgumentList.Add($DataDirectory)
}
[System.Diagnostics.Process]::Start($deskStart) | Out-Null
