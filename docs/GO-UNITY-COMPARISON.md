# Go unity-cli 비교

대상은 youngwoocho02/unity-cli의 Go 실행 파일과 자체 Connector이다. UnityBridge Desk는 측정기를 맡는다. 이는 CLI·Connector 조합의 비교이며 언어 자체의 성능 비교가 아니다.

## 사용법

1. 준비 탭에서 설치·활성화된 Unity 6를 선택한다.
2. **Go unity-cli 함께 비교 · 비공식**을 켠다. 기본 버전은 `0.4.1`이다. 입력한 버전의 GitHub 릴리스와 동일 태그의 Connector를 사용한다.
3. 비교할 UnityBridge 버전을 선택한다. 공식 Unity CLI + Pipeline도 선택할 수 있다. 합계 2~8개 대상을 선택한다.
4. 비교 기준과 F01~F04·S01을 선택하고 **벤치 시작**을 누른다.
5. 기존 결과 화면의 조건별 그래프·기준 대비 비교·시행 상세에서 `Go unity-cli v...`를 확인한다. 요약 TXT와 Excel에도 같은 대상 이름과 정리 결과를 남긴다.

API·생성형 AI는 이 실험에 포함하지 않는다. 알려지지 않은 버전은 실제 CLI 도움말·버전, Connector 패키지·보고 버전, 출력 검증을 통과해야 한다. 버전을 입력할 수 있다는 것이 모든 과거·미래 버전의 호환성을 보증하지는 않는다.

## 설치와 검증

GitHub 릴리스의 `unity-cli-windows-amd64.exe`를 `speed/go-work/<소유 ID>/artifacts`에 내려받는다. 태그가 가리키는 소스 커밋을 고정하고 해당 커밋에서 `unity-connector`만 추출한다. 게시자 SHA-256(제공 시), 실제 파일 SHA-256, CLI·Connector 폴더 전체 해시, 패키지 이름·버전을 검증한다. 소스의 MIT 고지도 보관한다. 전역 설치 스크립트를 실행하거나 PATH를 바꾸지 않는다.

빈 프로젝트에도 Connector가 컴파일되도록 Unity Test Framework와 Connector가 선언한 Newtonsoft JSON, 그 하위 의존 패키지를 함께 준비한다. Go v0.4.1의 TestRunner 어셈블리는 `UnityEditor.TestRunner`를 참조하지만 패키지 선언에는 Test Framework가 없어 Desk가 보완한다. 선택한 Editor에 포함된 패키지를 우선 사용하며 요구 버전보다 오래되면 중단한다. 나머지는 Unity 레지스트리의 고정 버전과 게시 해시로 확인한다. 각 패키지의 버전·출처·내용 해시를 실행 기록에 남기고 시행별 복사본을 프로젝트에 연결한다. Connector 원본 코드는 변경하지 않는다. 이 준비 시간은 명령 속도에 포함하지 않으며 내려받은 의존 패키지도 종료 후 함께 삭제한다.

각 시행은 CLI·Connector의 별도 복사본과 새 Unity 프로젝트를 만든다. 임시 파일·UPM 캐시·덤프 경로를 분리한다. 벤치 종료·실패·취소·미리 준비 후 창 종료 시 소유 정보가 확인되는 임시 보관함을 정리한다. 파일이 잠기거나 소유 정보가 불일치하면 삭제 완료로 표시하지 않는다. 비정상 종료의 잔여 폴더는 다음 준비 시 소유권과 프로세스 종료를 확인한 뒤 복구 정리한다.

## 작업과 측정 범위

- F01~F03: 기존 `DeskProbe.Run`을 그대로 공유한다. `[UnityCliTool]` 등록 부분과 CLI 인수·응답 해석만 다르다. 요청 식별값·프로젝트 경로·Unity PID와 정답을 확인한다.
- F04: 동일한 `SpeedExec.Source`와 요청 식별값 파일을 사용한다. Go는 원본 CLI가 지원하는 표준입력, UnityBridge·공식 Pipeline은 파일 입력을 사용한다. Go 표준입력 전달 시간은 CLI 시작부터 종료까지의 측정에 포함한다. 원본 코드 해시를 기록한다. 컴파일러는 각 제품의 기존 탐색 방식을 사용하므로 컴파일러 구현이 같다고 가정하지 않는다.
- S01: 비교 도구가 포함되면 공통 echo·응답 본문·오브젝트 생성/제거·exec·Editor 상태 작업을 사용한다. 사용자 지정 명령의 동등한 대응이 없거나 편집 준비 상태를 유지하지 않는 시나리오는 호환성 오류로 분리한다. CLI 동시 요청 수는 Unity의 내부 병렬 실행 수를 뜻하지 않는다.
- 측정: CLI 프로세스 시작부터 종료·출력 수집 완료까지이다. Go 원본 CLI의 연결 확인·버전 확인·재시도와 출력 직렬화도 포함한다. 다운로드·해시 확인·Unity 준비·별도 결과 검증·정리는 분리한다. 재시도 가능한 CLI의 성공 요청 수를 Unity 작업의 정확히 한 번 실행 횟수라고 해석하지 않는다.

## 사용자 설정과 연결 정보의 분리

확인한 v0.4.1은 `--instances-dir`, `--no-update-check`가 없으며 사용자 홈의 `.unity-cli`를 사용한다. 따라서 CLI 프로세스의 `USERPROFILE`·`HOME`을 시행별 `go-cli-profile`로 설정한다. 원본 Connector의 heartbeat를 읽어 프로젝트·프로세스 시작 시각·버전·포트를 확인하고, 이번 대상의 연결 정보 사본만 전용 홈에 준비한다. 사본 준비는 계측 밖에서 수행한다. 이후 CLI는 실제 `/health` 확인과 `/command` 요청을 수행한다. 측정 전후 원본 heartbeat를 다시 확인하므로 포트·프로세스 변경을 정상 결과로 처리하지 않는다. 컴파일·도메인 리로드 복구 성능은 이 고정 실험 범위가 아니다.

원본 CLI의 업데이트 확인 네트워크 비용이 기본 명령 시간에 섞이지 않도록 각 측정 묶음 직전에 시험 전용 `version-check.json`을 최신 확인 시각·선택 릴리스로 초기화한다. CLI 바이너리·Connector 소스는 변경하지 않는다. 캐시는 원본 CLI의 1시간 정책을 따른다. 하나의 측정 묶음이 1시간을 넘으면 업데이트 조회가 다시 발생할 수 있다. 결과의 환경 설명과 `go-cli-environment.json`에 이 조건을 기록한다.

Connector는 원래 사용자 홈의 `.unity-cli/instances`에도 heartbeat를 남긴다. Editor 종료를 확인한 후 이번 시험 프로젝트 경로와 PID가 모두 일치하는 JSON·JSON.tmp만 삭제한다. 기존 프로젝트의 연결 정보와 사용자 업데이트 캐시는 건드리지 않는다. Windows 시스템 캐시·Unity 라이선스·사용자 설정을 초기화하는 VM은 아니다.

## 검증 기록

2026-09-18 수정 후 빌드와 자동 검사 247개를 통과했다. 빈 프로젝트용 Test Framework 및 하위 의존성 준비, Editor 내장 버전 선택, 패키지 해시 변경·누락 검출과 시행별 복사를 검사한다. 기존 다운로드 무결성·압축 경로·임시 보관함 수명·응답과 대상 확인·개인 연결 정보 보존·앱 버튼·설정 저장 및 재사용 검사도 통과했다.

실제 Unity `6000.3.23f1`에서 Go `v0.4.1`과 UnityBridge `v0.3.0-rc.1`을 앱의 벤치 시작 버튼으로 실행했다. F01 첫/반복 호출, F02 1/10/100개 명령, F03 1KiB/64KiB/1MiB 응답, F04 첫/반복 exec와 공통 S01 5개 작업을 확인했다. 각 조건 반복 1회, 준비 호출 1회, 반복 측정 호출 2회, S01 작업당 4개 요청·동시 2개로 **30/30개 시행과 286/286개 측정 호출이 성공**했다. Go는 15개 시행 모두 통과했다. 사용한 의존성은 Editor 내장 Test Framework `1.6.0`·NUnit `2.0.5`, Unity 레지스트리 Newtonsoft JSON `3.2.1`이다.

결과 탭·현재 기록 선택·TXT·Excel·SVG 생성, 시행 폴더 30개와 Go CLI·Connector·의존 패키지 원본 삭제를 확인했다. 로컬 검증 ID는 `a7a25d0e-fb68-4294-997d-45a547ff42fe`이다. 이는 최소 반복의 기능 연동 검사이며 속도 우열이나 대규모 부하 안정성을 입증하는 실험은 아니다. 다른 Editor·Go 버전의 호환성을 보증하지 않는다. 이전 실패 기록은 보존되며 수정 앱에서 새 벤치를 시작해야 한다.

## 근거

- [Go CLI README: 커스텀 명령·exec·연결 방식](https://github.com/youngwoocho02/unity-cli)
- [v0.4.1 CLI: 입력·실제 호출·출력](https://github.com/youngwoocho02/unity-cli/blob/v0.4.1/cmd/root.go)
- [업데이트 확인 캐시](https://github.com/youngwoocho02/unity-cli/blob/v0.4.1/cmd/version_check.go)
- [인스턴스 탐색](https://github.com/youngwoocho02/unity-cli/blob/v0.4.1/internal/client/client.go)
- [Connector heartbeat·종료 동작](https://github.com/youngwoocho02/unity-cli/blob/v0.4.1/unity-connector/Editor/Heartbeat.cs)

각 실험은 같은 PC·Unity에서 순서를 바꿔 반복하며 성공·실패를 함께 남긴다. 실패를 0ms로 넣지 않고, 비교 평균에는 같은 블록에서 공동으로 검증된 결과만 사용한다. 단일 시험의 숫자로 전체 작업의 우열을 단정하지 않는다.
