param([Parameter(Mandatory=$true)][string]$BundlePath)
$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskBundle = (Resolve-Path -LiteralPath $BundlePath).Path
$deskManifest = Get-Content -LiteralPath (Join-Path $deskBundle 'distribution.json') -Raw | ConvertFrom-Json
foreach ($deskFile in $deskManifest.files) {
    $deskPath = Join-Path $deskBundle $deskFile.path
    if ((Get-FileHash -LiteralPath $deskPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $deskFile.sha256) { throw "Bundle integrity mismatch: $($deskFile.path)" }
}
$deskCompiler = (Get-Command gcc.exe -ErrorAction Stop).Source
$deskResourceCompiler = (Get-Command windres.exe -ErrorAction Stop).Source
$deskName = 'UnityBridgeDesk-전달용-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$deskPackage = Join-Path $deskRoot "dist/$deskName"
New-Item -ItemType Directory -Path (Join-Path $deskPackage 'app') | Out-Null
Get-ChildItem -LiteralPath $deskBundle -Force | Copy-Item -Destination (Join-Path $deskPackage 'app') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $deskRoot '.cache/package-build') -Force | Out-Null
# MinGW's linker accepts ANSI paths; keep its arguments ASCII and copy with Unicode-aware PowerShell.
Push-Location -LiteralPath $deskRoot
try {
    & $deskResourceCompiler 'build/DeskLauncher.rc' -O coff -o '.cache/package-build/DeskLauncher.res'
    if ($LASTEXITCODE -ne 0) { throw 'Launcher icon resource build failed' }
    & $deskCompiler 'build/DeskLauncher.c' '.cache/package-build/DeskLauncher.res' -o '.cache/package-build/DeskLauncher.exe' -municode -mwindows -static -Os -s -Wall -Wextra -Werror '-Wl,--no-insert-timestamp'
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }
}
finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $deskRoot '.cache/package-build/DeskLauncher.exe') -Destination (Join-Path $deskPackage 'UnityBridge Desk 실행.exe')
@'
UnityBridge Desk · 시작하기

1. ZIP 파일을 받았다면 먼저 「압축 풀기」를 하세요.
2. 「UnityBridge Desk 실행.exe」를 더블클릭하세요.

app 폴더는 프로그램이 사용하는 파일입니다. 실행 파일과 함께 두세요.
다른 사람에게는 ZIP 전체를 보내세요. 실행 파일 하나만 보내면 열리지 않습니다.

처음 사용하는 PC에서는 「벤치마크 → 1 준비 → 실험 환경 자동 준비」를 누르세요.
Bridge 0.2.0·0.2.1의 CLI와 Connector를 찾아서 보관하거나 공식 파일을 받아 등록합니다.
프로젝트가 여러 개라면 카드에서 선택하세요. Unity Editor는 프로젝트 버전에 맞게 설치되어 있어야 합니다.
「준비 완료」가 표시되면 아래 「N개 시행 시작」을 누르세요.
앱은 .NET 런타임을 포함합니다. 개인 Unity 프로젝트와 Unity Editor는 별도로 준비합니다.
이 ZIP에는 보내는 사람의 설정·실행 기록·인증 정보를 넣지 않습니다.

자세한 사용법: app 폴더의 README.md
결과 공유: 앱의 「3 결과」 → 「선택 결과 내보내기」
'@ | Set-Content -LiteralPath (Join-Path $deskPackage '처음 읽기.txt') -Encoding utf8BOM
$deskArchive = $deskPackage + '.zip'
Compress-Archive -LiteralPath $deskPackage -DestinationPath $deskArchive -CompressionLevel Optimal
Write-Output $deskPackage
Write-Output $deskArchive
