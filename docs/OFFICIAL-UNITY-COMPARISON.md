# 공식 Unity CLI + Pipeline 비교

앱의 기존 **준비 → 벤치 시작 → 결과 확인** 흐름에서 UnityBridge CLI + Connector와 **Unity 공식 독립 CLI + `com.unity.pipeline`**을 비교한다. Unity Hub CLI나 Editor의 `-executeMethod` 실행 시간과 혼합하지 않는다.

## 앱에서 실행

1. **UnityBridge Desk 실행.exe**를 열고 **준비 → 비교 대상**에서 **공식 Unity CLI + Pipeline 함께 비교**를 켠다.
2. 비교할 UnityBridge 릴리스를 1개 이상 선택한다. 공식 대상을 포함한 전체 대상 수는 2~8개다. 설치된 Unity 6가 발견되면 자동으로 선택하며, 여러 개면 Unity 환경에서 바꿀 수 있다.
3. **비교 기준**을 정하고 **벤치 시작**을 누른다. 공식 CLI·Pipeline과 의존 패키지 다운로드, 검증, 새 프로젝트 준비, 측정과 정리를 앱이 수행한다. CLI 경로나 설치 스크립트를 직접 지정할 필요는 없다.
4. 완료 후 **결과 확인**을 누른다. **조건별 시간 / 기준 버전 대비 / 시행 상세**에서 두 도구를 비교하고 **내보내기**에서 비교 요약 TXT·분석 Excel·보고서 폴더를 연다.

설치·활성화된 Unity 6는 필요하다. Editor 설치와 라이선스 로그인은 자동화하지 않는다. **공식 도구 버전 지정 (선택)**은 기본적으로 비워 두며, 시작 시 공식 목록에서 CLI와 Pipeline 버전을 선택하고 해당 실행에 고정한다. 재현할 버전이 정해져 있으면 두 입력칸에 정확한 버전을 지정한다. **파일 미리 준비**는 선택 사항으로, 시작 버튼만 눌러도 필요한 파일을 준비한다.

공식 대상 포함 여부와 버전·비교 기준은 다음 앱 실행에도 복원한다. 결과의 **설정 재사용**은 해당 실행에 기록된 정확한 CLI·Pipeline 버전까지 복원하며 자동으로 재실행하지 않는다. 공식 대상을 끄면 기존 UnityBridge 2~8개 버전 비교로 돌아간다. 기본 F01, 반복 N=2, 사전 실행 W=1, 측정 M=3에서 대상 두 개는 총 8개 시행이다.

공식 도구는 **벤치마다 새로 내려받아 임시 사용하고 종료 시 삭제**한다. 이전 실행의 파일을 재사용하지 않으므로 시작 준비에 인터넷 연결과 다운로드 시간이 필요하다. 같은 실행 전에 **파일 미리 준비**로 받은 파일은 검증 후 사용할 수 있다. 미리 준비만 하고 앱을 닫아도 임시 보관함을 정리한다. 다운로드·설치·삭제 시간은 명령 속도에 포함하지 않는다.

## 선택: 스크립트·자동화 실행

실행용 패키지의 `app` 폴더에서 PowerShell을 열어 다음을 실행한다. 소스에서는 먼저 Worker를 Release로 빌드한 뒤 `build/Compare-OfficialUnity.ps1`을 사용한다.

```powershell
.\Compare-OfficialUnity.ps1 -BridgeTags v0.2.1 -CliVersion 1.0.0-beta.10 -PipelineVersion 0.7.0-exp.1
```

기본 시험은 F01이며 반복 2회, 사전 실행 1회, 반복 조건의 측정 명령 3회다. 앱에서 선택했던 Unity 6를 우선 사용하고, 없으면 설치된 Unity 6를 찾는다. 명시적으로 고정하려면 `-EditorPath 'C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe'`를 추가한다. 실행 정책으로 스크립트가 차단되면 관리 정책을 바꾸지 않고 아래 Worker 입력 방식을 사용할 수 있다.

```powershell
.\Compare-OfficialUnity.ps1 -BridgeTags v0.2.0,v0.2.1 -Experiments F01,F02,F03,F04,S01 -Repeats 5 -Warmups 2 -Calls 10 -StressRequests 32 -StressConcurrency 4
```

버전을 생략하면 준비 시점의 공식 CLI beta 목록과 Pipeline 레지스트리에서 버전을 한 번 선택하고 고정한다. 재현 비교에서는 실제 기록에 남은 두 버전을 명시한다. 첫 UnityBridge 태그가 기준이며, `-OfficialBaseline`으로 공식 도구를 기준으로 설정할 수 있다. 전체 비교 도구 수는 2~8개다.

Worker를 직접 실행할 수도 있다.

```powershell
.\worker\UnityBridgeDesk.Worker.exe --compare-tools C:\Bench\comparison.json
```

```json
{
  "DataRoot": "C:\\Bench",
  "EditorPath": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.3.23f1\\Editor\\Unity.exe",
  "BridgeTags": ["v0.2.1"],
  "CliVersion": "1.0.0-beta.10",
  "PipelineVersion": "0.7.0-exp.1",
  "OfficialBaseline": false,
  "Options": {
    "Repeats": 2, "Warmups": 1, "Calls": 3,
    "TimeoutSeconds": 180, "PrepareSeconds": 600,
    "Experiments": ["F01", "F04"]
  }
}
```

기본 결과 루트는 `%LOCALAPPDATA%\UnityBridgeDesk`다. 실제 결과는 `speed/local-runs`, TXT·Excel·SVG 보고서는 `speed/reports`에 저장하며 종료 시 보고서 경로를 출력한다. 종료 코드 0은 전 시행 성공, 1은 준비 오류 또는 실패·중단·정리 실패가 있는 경우다. 측정 시작 전 사용자 취소는 130을 반환한다. Ctrl+C 중단은 진행 중인 시험의 프로세스 종료와 폴더 정리를 요청한다. 스크립트는 자동화용 선택 경로이며 앱 사용에 필요하지 않다.

## 같은 작업을 보내는 구조

앱의 시작 버튼 또는 자동화용 `OfficialComparison` → `SpeedBenchWorkflow`의 릴리스 준비 → `LocalSpeedCoordinator` → 별도 Worker → 새 Unity 프로젝트 → `SpeedGuest`의 공통 측정 루프 순으로 실행한다. 앱이 PowerShell 스크립트를 대신 실행하는 구조가 아니라 같은 준비·실행 서비스를 직접 호출한다. 공식 도구는 명령 인수·응답 해석·준비 상태 확인만 별도 어댑터를 사용한다. 반복 수, 실행 순서, 타이머, 검증, 실패 판정, 결과 저장·정리는 기존 실행기를 공유한다.

`DeskProbe.cs.txt` 한 파일에 공통 C# 작업을 둔다. UnityBridge에서는 `UnityBridgeTool`이 이를 호출하고, Pipeline에서는 `CliCommand`가 동일한 작업을 호출한다. Pipeline의 `MainThreadRequired=true`로 Unity 객체 작업을 메인 스레드에서 수행한다. Pipeline용 프로젝트에는 등록 방식을 고르는 컴파일 정의만 추가한다. 원본 fixture의 SHA-256은 두 대상에서 같다.

| 시험 | UnityBridge 경로 | 공식 CLI 경로 | 동일하게 고정하는 것 |
|---|---|---|---|
| F01 | `call desk_probe --params …` | `command … desk_probe --parameters …` | echo 42와 요청·대상 식별 정보 |
| F02 | 공통 probe의 create/move/inspect | 같은 probe의 create/move/inspect | 객체 1,000개, 1·10·100개 이동 명령, 최종 좌표 |
| F03 | probe payload | 같은 probe payload | 1 KiB·64 KiB·1 MiB의 동일 ASCII 본문 |
| F04 | `exec --file …` | `command … eval_file …` | 바이트 단위로 같은 C# 소스 파일과 반환값 |
| S01 | probe / `call exec` | 같은 probe / `command … eval` | 요청 수·동시 수·입력·기대 출력 |

S01의 기본 교차 도구 시험에서는 상태 조회도 공통 probe의 `state` 작업으로 통일한다. 서로 다른 내장 상태 API의 작업량을 같다고 가정하지 않는다. 사용자 지정 명령은 `desk_probe` 또는 `code`만 받는 `exec`의 대응만 지원한다. 대응이 정의되지 않은 명령·Play 상태 시나리오는 공식 대상에서 **벤치 호환성 오류**로 남기고 유효 시간과 완료율 평가에서 제외한다. 이름이 비슷한 공식 명령으로 임의 치환하지 않는다.

## 시간과 준비 완료의 경계

모든 측정 요청마다 새로운 CLI 프로세스를 시작한다. 프로세스 시작 직전부터 종료와 stdout/stderr 수집 완료까지의 경과 시간을 잰다. 공식 CLI의 지속 실행 `shell`, 분리 실행 `--detach`, 직접 HTTP 명령 실행은 속도 표본에 사용하지 않는다.

다운로드, 해시 검사, CLI 도움말·버전 조회, Unity 기동·패키지 해석·컴파일, 고정 500ms 안정화 대기와 응답 검증은 명령 시간에 포함하지 않는다. F02는 첫 이동 명령 시작부터 마지막 이동 명령 완료까지를 전체 작업 시간으로 집계한다. 생성·최종 상태 확인은 측정 밖이다. 준비 시간과 검증 시간은 별도 기록한다.

첫 명령 조건은 새 프로젝트의 새 Editor가 준비된 후, 사전 probe 호출 없이 보내는 첫 작업 명령이다. 반복 조건도 다른 새 프로젝트에서 W회 사전 실행 후 M회 측정한다. 도움말 조회가 CLI·OS 캐시를 데울 수 있으므로 차가운 OS/실행 파일 캐시 시험이라고 부르지 않는다.

UnityBridge는 기존 Connector의 준비 파일을 확인한다. Pipeline은 프로젝트 안의 공식 포트 설명자와 벤치의 Editor 상태 파일을 읽는다. 실제 PID·프로젝트·프로세스 시작 시각·Unity 버전·Pipeline 패키지 버전·컴파일 상태·최신 상태 시각을 확인한다. CLI의 `command` 목록 조회나 echo를 준비 호출로 보내지 않는다. Pipeline 상태 파일 작성은 준비 판정을 위한 관측 코드이며 제품 간 내부 구현 비용이 완전히 같다는 의미는 아니다.

응답은 각 도구의 성공 여부를 확인한 뒤 공통 데이터로 해석한다. Pipeline의 `eval`·`eval_file`은 명령 결과 안에 C# 실행 응답을 한 번 더 감싸므로 안쪽 성공 여부까지 확인한다. nonce, PID, 프로젝트 경로, 42·본문·처리 개수·최종 좌표 중 해당 시험의 기대 결과가 모두 맞아야 유효하다. S01 사용자 C#의 반환값은 지정한 기대 JSON으로 검증하며 nonce를 자동 삽입하지 않는다. 빠르게 오류를 반환한 요청은 빠른 성공으로 집계하지 않는다.

## 설치·격리·재현성

공식 설치 스크립트를 실행하거나 PATH를 수정하지 않는다. Unity 공식 CDN의 버전별 Windows x64 실행 파일을 다운로드하고 게시 SHA-256을 검증한다. Pipeline과 비내장 의존 패키지는 Unity 레지스트리에서 받아 게시 SHA-1을 확인한 뒤 SHA-256과 전체 폴더 해시도 기록한다.

선택한 Editor에 포함된 의존 패키지가 있으면 그 버전과 파일을 고정해 사용한다. 예를 들어 Pipeline 0.7.0-exp.1이 선언한 Test Framework 1.1.33을 Unity 6000.3에 강제 설치하면 API 불일치가 발생한다. 실제 확인한 6000.3.23f1에는 Test Framework 1.6.0과 NUnit 2.0.5가 포함됐으며 `editor-bundled` 출처·버전·해시를 남긴다. 고정된 Editor와 패키지 집합은 하나의 비교 조건이다. 의존 버전 충돌은 조용히 무시하지 않는다.

공식 CLI·Pipeline과 의존 패키지의 다운로드 원본·압축 파일은 Desk 전용 데이터 폴더의 `speed/official-work/<임시 ID>/artifacts`에 둔다. 기본 데이터 폴더는 `%LOCALAPPDATA%/UnityBridgeDesk`이며 사용자가 설치한 전역 CLI와 PATH를 사용하지 않는다. 실행 파일 옆의 설치 경로가 아니라 쓰기 가능한 앱 전용 하위 공간을 쓰므로 앱을 읽기 전용 위치에 두어도 같은 정책을 적용한다.

각 시행에는 원본을 `speed/local-work/<시행 ID>/release`로 새로 복사한다. 실제 CLI는 이 복사본에서만 실행하며 Pipeline은 해당 새 프로젝트에만 적용한다. 새 프로젝트·Editor·CLI·TEMP/TMP·UPM 캐시를 사용하고 시행 종료 후 프로세스 종료를 확인해 복사본·Library·로그·dump·캐시·프로젝트를 정리한다. 다운로드 원본은 실행하지 않으며 같은 벤치의 시행들에 동일한 바이트를 공급한 뒤 **벤치 종료·오류·취소 시 삭제**한다. 결과·진단 기록은 별도로 보관한다.

공식 CLI 프로세스에는 시행별 `cli-profile` 아래의 APPDATA·LOCALAPPDATA·HOME·USERPROFILE·XDG 경로와 임시 경로를 전달한다. 도움말 조회와 측정 호출 모두 같은 시행의 경로를 사용한다. 한 시행의 반복 호출 사이에는 상태를 유지하고 다음 시행에서는 새 폴더를 쓴다. 이 환경 변수는 Unity Editor나 사용자 PC 전체에 적용하지 않는다. Editor 라이선스 서비스, Windows 계정·레지스트리·시스템 캐시는 공유하므로 완전한 OS 격리나 차가운 캐시 시험은 아니다. 환경 변수 대신 Windows의 다른 경로 API를 쓰는 모든 파일 접근까지 차단하는 보안 기능도 아니다.

정리에는 소유 표식·경로 범위·프로세스 종료 확인과 재시도를 사용한다. 강제 종료로 남은 임시 보관함은 다음 공식 도구 준비 전에 소유 프로세스가 종료됐는지 확인해 복구한다. 사용 중이거나 소유권을 확인할 수 없으면 삭제·재사용하지 않고 실행을 막는다. 삭제 실패는 결과의 `OfficialCleanup`과 화면·TXT·Excel에 표시하며, 남은 임시 보관함을 성공적으로 정리하기 전에는 다음 벤치에 재사용하지 않는다. 이전 버전의 `speed/official-unity` 장기 보관함은 이 경로에서 읽지 않는다. 소유 표식이 없는 과거 자료와 사용자가 설치한 CLI를 임의 삭제하지 않는다.

토큰이 있는 Pipeline 포트 설명자는 결과에 복사하지 않는다. 공식 CLI의 경로 환경은 `official-cli-environment.json`에 기록하고 최종 임시 보관함의 삭제 여부를 실행 기록에 남긴다. UnityBridge의 검증된 배포 보관함과 읽기용 결과는 유지하며 실제 실행 복사본은 매 시행 삭제한다.

일부 Unity 구성 요소는 긴 Windows 경로를 처리하지 못한다. 기본 저장 위치 또는 `C:\Bench`처럼 짧은 경로를 권장한다. 임시 프로젝트를 깊은 저장소 폴더 안에 두면 Pipeline의 긴 DLL 이름에서 준비 오류가 날 수 있다. 기존 프로젝트나 Windows 전체 캐시를 삭제해 해결하지 않는다.

## 해석과 공식 근거

이 비교는 **CLI + Editor 연결 패키지 + 같은 작업**의 완료 시간을 비교한다. Unity 엔진 연산만의 속도나 모든 내장 도구의 우열을 뜻하지 않는다. CLI의 JSON 포장·인수 해석·직렬화 구조 차이는 실제 도구 경로의 비용에 포함된다. 양쪽 모두 같은 블록에서 성공한 표본만 기준 대비 비교에 사용한다. 최소 반복의 연동 점검은 성능 우열의 근거가 아니다.

- [Unity CLI 소개](https://unity.com/blog/meet-the-unity-cli): 독립 CLI, `CliCommand`, `eval_file`, Unity 6 지원.
- [공식 CLI 참조](https://docs.unity.com/en-us/unity-cli/unity-cli-reference): JSON 출력, 업데이트 확인 비활성화, `shell`과 일반 호출의 차이. 설치된 버전의 도움말도 별도 보관한다.
- [Pipeline 명령 작성](https://docs.unity3d.com/Packages/com.unity.pipeline@0.7/manual/creating-commands.html): 같은 C# 작업을 명령으로 등록하는 방식과 메인 스레드 지정.
- [Pipeline 연결 규약](https://docs.unity3d.com/Packages/com.unity.pipeline@0.7/manual/connectivity.html): 프로젝트별 포트 설명자, PID·경로·로컬 인증 토큰.

공식 도구는 실험 단계 제품이므로 이후 릴리스의 인수·출력 형식 변경은 호환성 확인을 거쳐 지원한다. 확인되지 않은 응답을 성공으로 간주하지 않는다.
