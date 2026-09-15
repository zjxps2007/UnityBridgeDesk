# UnityBridge Desk v0.3.0 · 속도 벤치 전용

ZIP을 모두 압축 해제한 뒤 루트의 **UnityBridge Desk 실행.exe**를 실행하세요. `app` 폴더를 옆에 유지하세요.

1. 준비 탭에서 자동으로 찾은 Unity를 확인합니다.
2. 비교할 정식·RC 릴리스 두 개 이상을 선택합니다.
3. **벤치 시작**을 누릅니다.
4. 결과 탭에서 시간·성공 여부·정리 상태를 확인합니다.

설치·활성화된 Unity Editor가 필요합니다. 다른 위치는 실행 파일 선택으로 지정할 수 있습니다. 가상머신·ISO·BIOS 변경·별도 Windows 계정 없이 실행하며 .NET 런타임은 포함돼 있습니다.

실험마다 새 프로젝트와 임시 폴더를 준비하고 Unity·CLI를 새로 실행합니다. 종료 시 시험 프로세스와 폴더를 정리합니다. Windows 사용자 설정·공용 서비스·시스템 캐시는 공유하며 보안 샌드박스가 아닙니다.

[벤치 설계의 근거와 README](BENCHMARK-README.md) · [사용법](docs/USAGE.md) · [측정 명세](docs/BENCHMARKS.md) · [검증과 제한](docs/LOCAL-BENCH.md) · [RC 설치 형식](docs/RC-INSTALL.md) · [v0.3.0 변경 사항](CHANGELOG.md)

기본 데이터: `%LOCALAPPDATA%/UnityBridgeDesk/speed`. 새 결과는 `local-runs`에 저장됩니다. 원시 자료에 개인 경로가 포함되므로 공개 전 확인하세요. 이 ZIP에는 개인 설정·결과·인증 정보가 포함되지 않습니다.
