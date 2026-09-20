param([Parameter(Mandatory=$true)][string]$BundlePath,[string]$OutputRoot)
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
$deskPackage = if ($OutputRoot) { Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $deskName } else { Join-Path $deskRoot "dist/$deskName" }
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
공식 Unity와 비교하려면 「공식 Unity CLI + Pipeline 함께 비교」를 켜고
UnityBridge 릴리스를 한 개 이상 선택해 같은 「벤치 시작」을 누르세요.
Go 도구와 비교하려면 「Go unity-cli 함께 비교 · 비공식」을 켜세요.
기본 Go 버전은 0.4.1이며 별도 Go 설치 없이 CLI·Connector·필수 Unity 패키지를 자동 준비합니다.
빈 프로젝트의 Test Framework 누락으로 Go 측정이 실패하던 문제를 수정했습니다.
Unity 6000.3.23f1에서 실제 연동 30개 시행을 통과했습니다. 상세 조건은 app/docs/GO-UNITY-COMPARISON.md를 확인하세요.
설치·활성화된 Unity 6가 필요하며 공식 도구 파일도 자동 준비합니다.
공식 도구는 앱 전용 임시 보관함에 내려받고 벤치 종료·오류·취소 시 삭제합니다.
다음 공식 비교에는 다시 다운로드하므로 인터넷 연결이 필요합니다.
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
공식 Unity CLI + Pipeline 비교: 앱의 준비 탭 · app/docs/OFFICIAL-UNITY-COMPARISON.md
Go unity-cli 비교: 앱의 준비 탭 · app/docs/GO-UNITY-COMPARISON.md
결과 확인: 앱의 「03 결과」 → 평균 차이와 그래프 → 내보내기
좌측에서 실험·조건·시행 번호를 선택하고 중앙에서 결과를 확인하세요.
번호 검색과 버전·상태 필터를 지원하며 「실행 기록」에서 과거 결과를 고릅니다.
좁은 창에서 접힌 목록은 「목록」 또는 Alt+0으로 펼치세요.
내보내기 메뉴에서 비교 요약 TXT·복사·분석 Excel·현재 그래프 저장을 선택하세요.
결과 폴더에는 요약 TXT·분석 Excel·실패 내역·SVG 그래프·04_측정근거.txt가 저장됩니다.
반복 설계는 준비의 「반복 설계용 예비 설정」에서 시작합니다. 자동으로 실행하지 않습니다.
결과의 「기준 버전 대비 → 측정 신뢰도와 다음 실험」에서 신뢰구간과 다음 실험을 확인하세요.
수식·가정·논문 적용 범위는 app/docs/STATISTICAL-METHODOLOGY.md에 있습니다.
연구 검증은 준비의 「연구·측정기 검증」에서 목적을 선택합니다. 동일 릴리스 A/A는 Bridge 하나만 선택합니다.
연구 자료 ZIP과 별도 Python 검산은 app/docs/RESEARCH-VALIDATION.md를 확인하세요.
연구 기능은 미배포 보완입니다. 실제 Unity A/A·독립 세션 재현과 논문 적합성 인증을 완료한 것은 아닙니다.
JSON·CSV·로그는 「내보내기 → 원본·로그 폴더 열기」에서 확인하세요.
테마·캐릭터 표시·움직임은 오른쪽 위 「설정」에서 변경합니다.
「캐릭터」에서 고양이 아라와 여우 아샤를 각각 또는 함께 켤 수 있습니다.
탐색과 활동량도 각각 고릅니다. 자세한 동작은 app/docs/CHARACTERS.md를 확인하세요.
마스코트는 포인터·클릭·Enter·Space에 반응하며 벤치 중에는 움직임을 멈춥니다.
원시 결과에는 개인 경로가 포함될 수 있습니다. 자동 익명화 내보내기는 아직 제공하지 않습니다.
'@
$deskStartGuide.Replace('UnityBridge Desk ·',"UnityBridge Desk v$deskVersion ·") | Set-Content -LiteralPath (Join-Path $deskPackage '처음 읽기.txt') -Encoding utf8BOM
$deskArchive = $deskPackage + '.zip'
Compress-Archive -LiteralPath $deskPackage -DestinationPath $deskArchive -CompressionLevel Optimal
Write-Output $deskPackage
Write-Output $deskArchive
