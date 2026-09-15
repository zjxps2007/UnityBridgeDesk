# 외부 구성 요소와 자산

- **UnityBridge**: [zjxps2007/UnityBridge](https://github.com/zjxps2007/UnityBridge). 이 앱이 연결하는 외부 도구입니다. CLI·Connector 소스나 바이너리를 이 저장소와 ZIP에 포함하지 않습니다. 사용자가 **실험 환경 자동 준비**를 누르면 공식 0.2.0·0.2.1 릴리스와 고정 커밋의 Connector를 내려받거나 검증된 로컬 파일을 복사합니다. 사용하는 배포본의 라이선스와 고지를 확인하세요.
- **Unity Editor / Unity Hub**: 사용자가 별도로 설치합니다. 프로그램·글꼴·라이선스를 이 저장소에 포함하지 않습니다.
- **Codex CLI**: AI 기능에서 사용자가 지정하는 외부 실행 도구입니다. 바이너리·계정·인증 파일을 포함하지 않습니다.
- **.NET**: 실행용 배포본에는 Microsoft .NET 런타임과 해당 패키지의 `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT`가 포함됩니다.
- **MSTest / Microsoft.NET.Test.Sdk**: 자동 검사 의존성입니다. 버전과 의존성 목록은 테스트 프로젝트 및 `packages.lock.json`에 기록합니다.
- **화면 자산**: 생성 배경과 프로젝트의 구름 고양이 벡터를 사용합니다. 출처·변환 기록은 [자산 설명](src/UnityBridgeDesk.Desktop/Assets/README.md)에 있습니다.

UnityBridge Desk는 [MIT 라이선스](LICENSE)를 사용합니다. [표준 MIT 본문](https://opensource.org/license/mit)을 사용하며, 이 프로젝트의 라이선스가 외부 구성 요소의 라이선스를 대체하지는 않습니다.
