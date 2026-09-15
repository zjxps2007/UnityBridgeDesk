# 개발과 배포

## 환경과 기본 실행

Windows x64, PowerShell 7, `global.json`에 고정한 .NET SDK 10.0.400을 사용합니다. 전달용 루트 실행기를 만들 때만 MinGW-w64의 `gcc.exe`·`windres.exe`가 필요합니다.

```powershell
pwsh -File ./build/Verify.ps1 -Restore
pwsh -File ./build/StartDesk.ps1
```

처음에는 잠금 파일로 의존성을 복원합니다. 복원 이후에는 `-Restore`를 생략할 수 있습니다. 캐시·합성 자료는 `.cache/`, 자동 검사 결과는 `TestResults/`에 저장합니다. 기본 화면은 SpeedBenchWindow이며 속도 벤치만 제공합니다.

테스트 호스트 진단은 `.cache/test-diagnostics/<ID>/`에 저장합니다. 테스트 수가 통과해도 종료 과정에 처리되지 않은 예외가 있으면 Verify는 실패합니다. WPF 검사는 저장·창 종료·스레드 종료까지 기다립니다. 자동 검사에서 실제 VM·Unity 또는 유료 AI를 실행하지 않습니다.

다른 앱 데이터 폴더를 사용하려면:

```powershell
pwsh -File ./build/StartDesk.ps1 -DataDirectory 'C:/DeskSandbox/AppData'
```

## 새 실행 구조

```text
Desktop / SpeedBenchWindow
  ├─ ReleaseRepository: 공식 릴리스 보관함
  └─ LocalSpeedCoordinator → LocalWorkspace: 시행별 파일 준비·소유 확인·정리
       └─ WorkerRunner → Windows Job Object → Worker --local-trial → SpeedGuest
            ├─ UnityEnvironment: Unity 기동·ready
            └─ TimedProcessRunner: CLI 실행·계측
  SpeedAnalysis: 조건별 성공 통계·블록 비교·CSV
```

새 구현은 `src/UnityBridgeDesk.Infrastructure/SpeedBench/`와 Desktop의 `SpeedBenchWindow.xaml(.cs)`에 있습니다. 설정은 `speed/local-settings.json`, 기록은 `speed/local-runs/`를 사용합니다. 이전 MainWindow·DeskRuntime·AI·VirtualBox 경로는 회귀 검사와 기록 호환성을 위해 소스에 남겨두었으며 기본 화면에 연결하지 않습니다.

`Worker --local-trial`은 소유 표식, 요청·결과 경로, 자식 프로세스에 전달한 TEMP/TMP·UPM 환경을 확인합니다. WorkerRunner의 시작 허가 전에 Job Object에 연결하며 취소·종료 시 묶음의 프로세스가 종료될 때까지 기다립니다. 기존 개인 Unity 프로세스를 검색해 종료하지 않습니다. 개발 실행은 같은 구성으로 빌드한 Worker 출력을 사용하고, 배포본은 `worker/`를 사용합니다.

기존 `Worker --vm-trial`은 여전히 게스트 표식을 요구합니다. `--fixture`, `--smoke`, `--smoke-ai`, `--integration` 등은 별도 진단 경로이며 현재 로컬 속도 결과와 구분합니다.

## 검증 구분

`SpeedBenchTests`는 실제 Windows 자식 프로세스의 타이머·출력·실패를 확인합니다. `LocalSpeedTests`는 새 폴더 생성, 재사용·범위 밖 삭제 거부, 살아 있는 프로세스 보호, 이전 폴더 복구, 해당 프로젝트의 heartbeat만 정리하는지 검사합니다. `ProcessTests`는 취소 시 손자 프로세스까지 종료하는지 확인합니다. 릴리스 서비스의 일반 자동 검사는 가짜 HTTP 서버와 실행하지 않는 PE 파일을 사용합니다. 이전 VM 제어 검사는 합성 응답을 사용하는 회귀 검사입니다.

공식 릴리스 다운로드와 실제 설치된 Unity를 사용하는 통합 확인은 별도 임시 데이터 폴더에서 수행하고 일반 CI와 구분합니다. 파일 준비 성공만으로 Unity 호환성이나 속도를 입증하지 않습니다. 완료한 실제 시험 범위와 남은 한계는 [LOCAL-BENCH.md](LOCAL-BENCH.md)에 기록합니다.

기존 `BridgeEnvironmentSetup`의 0.2.0·0.2.1 고정 정의는 이전 기능용입니다. 새 UI는 `ReleaseRepository`로 공식 목록을 조회하고 태그가 가리키는 커밋을 고정합니다. 게시자 자산 해시가 제공되는 경우 대조하며 CLI와 Connector의 실제 내용 해시를 기록합니다.

`CliDistribution`은 ZIP·기존 EXE 자산 우선순위, RC 폴더 구성과 전체 해시, RC1의 제한된 보고 버전 예외를 담당합니다. `ReleaseBundleTests`는 런타임 전체 전달, 손상·누락·잘못된 구조와 SHA 거부, 오프라인 재사용·복구를 검사합니다. [RC 설치 명세](RC-INSTALL.md)를 확인하세요.

## 실행용 ZIP

```powershell
pwsh -File ./build/Publish-Desk.ps1
# 위 명령이 출력한 실제 경로를 사용합니다.
pwsh -File ./build/Package-Desk.ps1 -BundlePath 'C:/.../UnityBridgeDesk-win-x64-날짜시간'
pwsh -File ./build/Verify-Package.ps1 -PackagePath './dist/UnityBridgeDesk-v0.3.0-win-x64'
```

Publish는 .NET 런타임을 포함한 Desktop·Worker와 사용 문서를 만듭니다. 버전은 `Directory.Build.props`를 기준으로 고정하며 배포 명세에 기록합니다. Package는 상단의 **UnityBridge Desk 실행.exe**, **처음 읽기.txt**, **app/**로 구성한 `UnityBridgeDesk-v0.3.0-win-x64.zip`을 만듭니다. Verify-Package는 실행 파일 버전·배포 해시·ZIP 내용과 옮긴 한글 경로에서의 실행 대상·인수 전달을 검사합니다. 같은 버전 폴더와 ZIP은 덮어쓰지 않습니다.

현재 배포는 **v0.3.0 정식**입니다. 기본 실험은 예비 설정이며 분석은 기술 통계입니다. 실제 Unity 통합 시험 범위와 다양한 PC·Unity 버전에서 남은 확인 범위는 검증 문서에 구분합니다. Unity·Bridge 바이너리, Windows 이미지, 개인 데이터는 동봉하지 않습니다. 라이선스와 외부 구성 요소 고지, .NET 런타임 고지는 함께 보관합니다.

## 소스·아이콘·CI

`build/Export-Source.ps1`은 공개할 소스·문서만 새 폴더와 ZIP으로 내보냅니다. `speed/`, VM 디스크·ISO·개인 기록을 공개 소스로 올리지 않습니다. [GitHub 안내](PUBLISHING.md)를 참고하세요.

아이콘은 `design/branding/desk-icon.xaml`에서 `pwsh -STA -File ./build/Build-Icon.ps1`로 다시 만들 수 있습니다. 기본 빌드는 저장된 아이콘을 사용합니다.

`.github/workflows/verify.yml`은 Windows 빌드·자동 검사를 정의합니다. 실제 GitHub 통과 여부는 업로드 후 Actions에서 확인해야 합니다. VM·Unity 설치와 자동 배포는 하지 않습니다.
