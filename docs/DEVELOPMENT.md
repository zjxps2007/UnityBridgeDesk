# 개발과 배포

## 환경

- Windows x64: WPF와 Windows 프로세스 관리 API를 사용합니다.
- .NET SDK: `global.json`에 고정한 `10.0.400`.
- PowerShell 7: 빌드·검사·패키징 스크립트 실행.
- 전달용 실행기 제작에만 MinGW-w64의 `gcc.exe`와 `windres.exe`가 필요합니다.

## 빌드와 검사

저장소 루트에서 실행합니다.

```powershell
pwsh -File ./build/Verify.ps1 -Restore
pwsh -File ./build/StartDesk.ps1
```

첫 명령은 잠금 파일을 사용해 의존성을 복원하고 전체 빌드·검사를 수행합니다. 복원 후에는 `-Restore`를 생략할 수 있습니다. 캐시와 합성 시험 자료는 `.cache/`, 결과는 `TestResults/`에 저장합니다. 자동 검사에는 사용자 Unity 프로젝트 실행과 유료 AI 호출이 포함되지 않습니다.

테스트 호스트 진단은 실행마다 `.cache/test-diagnostics/<실행 ID>/`에 저장합니다. 테스트 개수가 모두 통과해도 종료 과정에서 처리되지 않은 예외가 기록되면 검증 스크립트는 실패합니다. WPF 검사는 화면 담당 스레드에서 비동기 작업을 이어 받고, 저장·창 종료·스레드 종료까지 기다립니다. 저장을 지연시킨 종료와 릴리스 선택·복원도 검사합니다.

별도 앱 데이터를 사용하려면 다음처럼 실행합니다.

```powershell
pwsh -File ./build/StartDesk.ps1 -DataDirectory 'C:/DeskSandbox/AppData'
```

기본 앱 데이터는 `%LOCALAPPDATA%/UnityBridgeDesk`입니다. `--data-dir`에 지정하는 디렉터리를 소스에 커밋하지 마세요.

## 프로젝트 구조

```text
src/
  UnityBridgeDesk.Core/             실행 계획·상태·식별자·공통 계약
  UnityBridgeDesk.Infrastructure/   탐색·보관함·Bridge·AI·벤치·저장
  UnityBridgeDesk.Desktop/          WPF 작업실·입력·진행·결과 UI
  UnityBridgeDesk.Worker/           격리된 작업 프로세스와 명시적 진단
tests/                             Core·Infrastructure·Desktop 검사
build/                             빌드·배포·아이콘·소스 내보내기
docs/                              사용·측정·개발·GitHub 업로드 안내
design/branding/                   구름 고양이 아이콘 벡터·미리보기
```

UI는 세 도구의 초안·선택을 보존합니다. 검토 완료 시 실행 계획을 스냅샷으로 만들고 Worker가 실행합니다. 계획 이후의 보관함 선택이 진행 중 작업을 바꾸지 않습니다. 파일과 프로세스 정리가 완료되지 않으면 다음 시행을 중지합니다.

Worker의 `--fixture`, `--smoke`, `--smoke-ai`, `--integration` 등은 명시적으로 호출하는 진단 경로입니다. 기본 UI와 자동 CI가 실제 Unity 진단을 실행하지 않습니다. `--smoke-ai`·통합 진단의 합성 제공자를 실제 생성형 AI 결과로 해석하지 마세요.

## 실행용 ZIP 제작

### 자동 환경 준비 검증

`BridgeEnvironmentSetup`은 공식 Windows x64 CLI 해시와 고정 커밋의 Connector 폴더 해시를 기준으로 검색·다운로드·검증·보관합니다. 현재 기본 정의는 0.2.0·0.2.1이며 `connector-tree-v1`을 사용합니다. 새 기본 버전을 추가할 때는 공식 릴리스의 자산 SHA-256, 태그가 가리키는 커밋, 해당 커밋에서 가져온 Connector 해시를 확인해야 합니다. 실행 파일 이름이나 로컬 `provenance.json`만으로 공식 버전을 인정하지 않습니다.

`BridgeSetupTests`는 가짜 HTTP 응답과 실행하지 않는 PE 파일로 첫 다운로드, 로컬 복사, 오프라인 재사용, 손상 복구, 취소·재시도, 두 번째 버전 실패, 원자적 등록, 압축 경로 이탈 거부를 검증합니다. WPF 검사는 실제 자동 준비 버튼을 눌러 두 버전 선택, 프로젝트 단일 후보와 복수 후보, Editor 누락 안내, 중복 등록 방지와 원본 프로젝트 미변경을 확인합니다. 일반 검사에는 실제 네트워크 다운로드가 없습니다.

실제 공식 다운로드와 설치된 Editor를 사용하는 준비 확인은 별도 임시 데이터 폴더에서 수행했습니다. 두 버전의 다운로드·해시 검증·등록·오프라인 재사용 및 준비 완료 후 `8개 시행 시작` 표시를 확인했으며 Unity·Worker·AI는 실행하지 않았습니다.

### 패키징 명령

```powershell
pwsh -File ./build/Publish-Desk.ps1
# 위 명령이 출력한 경로를 BundlePath에 넣습니다.
pwsh -File ./build/Package-Desk.ps1 -BundlePath './dist/UnityBridgeDesk-win-x64-날짜시간'
pwsh -File ./build/Verify-Package.ps1 -PackagePath './dist/UnityBridgeDesk-전달용-날짜시간'
```

`Publish-Desk.ps1`은 .NET 런타임을 포함한 x64 배포 폴더를 만듭니다. `Package-Desk.ps1`은 상단의 **UnityBridge Desk 실행.exe**, **처음 읽기.txt**, **app/**로 구성한 ZIP을 만듭니다. 배포 해시, ZIP 내용, 옮긴 한글 경로와 인수 전달은 마지막 명령으로 검사합니다. Unity·Bridge·Codex 바이너리와 개인 데이터는 포함하지 않습니다.

MIT 라이선스와 프로젝트 외부 고지는 배포본에 각각 `LICENSE`, `THIRD_PARTY_NOTICES.md`로 복사합니다. .NET 런타임의 `LICENSE.TXT`와 `THIRD-PARTY-NOTICES.TXT`는 별도로 보존합니다.

## 아이콘 재생성

```powershell
pwsh -STA -File ./build/Build-Icon.ps1
```

`design/branding/desk-icon.xaml`에서 미리보기 PNG와 9개 크기의 `Assets/desk.ico`를 생성합니다. 기본 빌드에는 저장소에 포함된 아이콘을 사용하므로 재생성은 필수가 아닙니다.

## GitHub 검사

`.github/workflows/verify.yml`은 push·pull request와 수동 실행에 대해 Windows 빌드·합성 검사를 정의합니다. SDK는 `global.json`을 읽습니다. 자동 배포·Unity 설치·유료 AI 호출은 수행하지 않습니다. 워크플로 파일을 추가한 것과 GitHub에서 실제 통과한 것은 별개이며 업로드 후 Actions에서 결과를 확인하세요.

공식 액션 문서: [checkout](https://github.com/actions/checkout), [setup-dotnet](https://github.com/actions/setup-dotnet).
