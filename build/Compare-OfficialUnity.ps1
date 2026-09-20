param(
    [string[]]$BridgeTags = @('v0.2.1'),
    [string]$EditorPath = '',
    [string]$CliVersion,
    [string]$PipelineVersion,
    [string[]]$Experiments = @('F01'),
    [int]$Repeats = 2, [int]$Warmups = 1, [int]$Calls = 3,
    [int]$StressRequests = 32, [int]$StressConcurrency = 4,
    [int]$TimeoutSeconds = 180, [int]$PrepareSeconds = 600,
    [switch]$OfficialBaseline,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'UnityBridgeDesk')
)
$ErrorActionPreference = 'Stop'
$deskCandidates = @(
    (Join-Path $PSScriptRoot 'worker/UnityBridgeDesk.Worker.exe'),
    (Join-Path $PSScriptRoot '../src/UnityBridgeDesk.Worker/bin/Release/net10.0-windows/UnityBridgeDesk.Worker.exe')
)
$deskWorker = $deskCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $deskWorker) { throw 'Build the Worker in Release, or use this script next to the published worker folder.' }
$deskData = [IO.Path]::GetFullPath($DataRoot)
$deskRequestFolder = Join-Path $deskData 'speed/comparisons'
New-Item -ItemType Directory -Path $deskRequestFolder -Force | Out-Null
$deskRequest = Join-Path $deskRequestFolder (([Guid]::NewGuid().ToString('N')) + '.json')
$deskConfig = @{
    DataRoot=$deskData; EditorPath=$EditorPath; BridgeTags=$BridgeTags; OfficialBaseline=[bool]$OfficialBaseline
    CliVersion=$(if ($CliVersion) { $CliVersion } else { $null })
    PipelineVersion=$(if ($PipelineVersion) { $PipelineVersion } else { $null })
    Options=@{Repeats=$Repeats;Warmups=$Warmups;Calls=$Calls;Experiments=$Experiments;
        StressRequests=$StressRequests;StressConcurrency=$StressConcurrency;TimeoutSeconds=$TimeoutSeconds;PrepareSeconds=$PrepareSeconds}
}
$deskConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $deskRequest -Encoding utf8
& $deskWorker --compare-tools $deskRequest
exit $LASTEXITCODE
