param([Parameter(Mandatory=$true)][string]$BundlePath)
$ErrorActionPreference = 'Stop'
$deskRoot = Split-Path -Parent $PSScriptRoot
$deskBundle = (Resolve-Path -LiteralPath $BundlePath).Path
$deskManifest = Get-Content -LiteralPath (Join-Path $deskBundle 'distribution.json') -Raw | ConvertFrom-Json
$deskVersion = [string]$deskManifest.version
$deskSourceVersion = [string]([xml](Get-Content -LiteralPath (Join-Path $deskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($deskVersion -notmatch '^\d+\.\d+\.\d+$' -or $deskVersion -ne $deskSourceVersion) { throw 'Publish the current Desk version before packaging.' }
foreach ($deskFile in $deskManifest.files) {
    $deskPath = Join-Path $deskBundle $deskFile.path
    if ((Get-FileHash -LiteralPath $deskPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $deskFile.sha256) { throw "Bundle integrity mismatch: $($deskFile.path)" }
}
$deskCompiler = (Get-Command gcc.exe -ErrorAction Stop).Source
$deskResourceCompiler = (Get-Command windres.exe -ErrorAction Stop).Source
$deskName = "UnityBridgeDesk-v$deskVersion-win-x64"
$deskPackage = Join-Path $deskRoot "dist/$deskName"
if ((Test-Path -LiteralPath $deskPackage) -or (Test-Path -LiteralPath ($deskPackage + '.zip'))) { throw 'This version is already packaged. Use a new version or explicitly preserve the previous package elsewhere.' }
New-Item -ItemType Directory -Path (Join-Path $deskRoot '.cache/package-build') -Force | Out-Null
# MinGW's linker accepts ANSI paths; keep its arguments ASCII and copy with Unicode-aware PowerShell.
Push-Location -LiteralPath $deskRoot
try {
    $deskVersionTuple = ($deskVersion.Replace('.',',') + ',0')
    @("#define DESK_VERSION_NUM $deskVersionTuple",('#define DESK_VERSION_TEXT "' + $deskVersion + '\0"')) | Set-Content -LiteralPath '.cache/package-build/DeskVersion.h' -Encoding ascii
    & $deskResourceCompiler 'build/DeskLauncher.rc' -I '.cache/package-build' -O coff -o '.cache/package-build/DeskLauncher.res'
    if ($LASTEXITCODE -ne 0) { throw 'Launcher icon resource build failed' }
    & $deskCompiler 'build/DeskLauncher.c' '.cache/package-build/DeskLauncher.res' -o '.cache/package-build/DeskLauncher.exe' -municode -mwindows -static -Os -s -Wall -Wextra -Werror '-Wl,--no-insert-timestamp'
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }
}
finally { Pop-Location }
New-Item -ItemType Directory -Path (Join-Path $deskPackage 'app') | Out-Null
Get-ChildItem -LiteralPath $deskBundle -Force | Copy-Item -Destination (Join-Path $deskPackage 'app') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $deskRoot '.cache/package-build/DeskLauncher.exe') -Destination (Join-Path $deskPackage 'UnityBridge Desk 실행.exe')
$deskStartGuide = @'
UnityBridge Desk · 속도 벤치 전용 · 시작하기

1. ZIP 파일을 받았다면 먼저 「압축 풀기」를 하세요.
2. 「UnityBridge Desk 실행.exe」를 더블클릭하세요.

app 폴더는 프로그램이 사용하는 파일입니다. 실행 파일과 함께 두세요.
다른 사람에게는 ZIP 전체를 보내세요. 실행 파일 하나만 보내면 열리지 않습니다.

설치·활성화된 Unity를 자동으로 찾습니다.
비교할 릴리스를 두 개 이상 선택한 뒤 「벤치 시작」을 누르세요.
매번 새 임시 프로젝트에서 측정하고 결과를 저장한 뒤 정리합니다.
정식 EXE와 RC ZIP의 런타임 전체를 자동으로 준비합니다.
가상머신·ISO·BIOS 설정·별도 계정이 필요하지 않습니다.
Windows 사용자 설정과 공용 캐시는 공유합니다.
앱은 .NET 런타임을 포함합니다.
이 ZIP에는 보내는 사람의 설정·실행 기록·인증 정보를 넣지 않습니다.

자세한 사용법: app 폴더의 README.md
상세 안내: app/docs/USAGE.md · app/docs/LOCAL-BENCH.md
벤치 근거: app/BENCHMARK-README.md
RC 설치 변경: app/docs/RC-INSTALL.md
결과 확인: 앱의 「3 결과」 → 「JSON·CSV 결과 폴더」
원시 결과에는 개인 경로가 포함될 수 있습니다. 자동 익명화 내보내기는 아직 제공하지 않습니다.
'@
$deskStartGuide.Replace('UnityBridge Desk ·',"UnityBridge Desk v$deskVersion ·") | Set-Content -LiteralPath (Join-Path $deskPackage '처음 읽기.txt') -Encoding utf8BOM
$deskArchive = $deskPackage + '.zip'
Compress-Archive -LiteralPath $deskPackage -DestinationPath $deskArchive -CompressionLevel Optimal
Write-Output $deskPackage
Write-Output $deskArchive
