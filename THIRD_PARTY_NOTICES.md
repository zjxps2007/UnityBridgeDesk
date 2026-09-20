# 외부 구성 요소와 자산

- **UnityBridge**: [zjxps2007/UnityBridge](https://github.com/zjxps2007/UnityBridge). 이 앱이 연결하는 외부 도구입니다. CLI·Connector 소스나 바이너리를 이 저장소와 ZIP에 포함하지 않습니다. 사용자가 **실험 환경 자동 준비**를 누르면 공식 0.2.0·0.2.1 릴리스와 고정 커밋의 Connector를 내려받거나 검증된 로컬 파일을 복사합니다. 사용하는 배포본의 라이선스와 고지를 확인하세요.
- **Unity Editor / Unity Hub**: 사용자가 별도로 설치합니다. 프로그램·글꼴·라이선스를 이 저장소에 포함하지 않습니다.
- **Go unity-cli (비공식)**: [youngwoocho02/unity-cli](https://github.com/youngwoocho02/unity-cli), MIT. 비교를 선택하면 지정 릴리스의 Windows 실행 파일과 같은 태그·고정 커밋의 Connector를 임시 다운로드합니다. 원본 라이선스 고지를 함께 보관하고 시험 종료 후 정리합니다. 저장소·실행용 ZIP에 이 외부 바이너리나 Connector를 동봉하지 않습니다.
- **공식 Unity CLI / Unity Pipeline**: 비교를 실행할 때 공식 CDN·Unity 패키지 레지스트리에서 버전을 고정해 준비하는 외부 도구입니다. 선택한 Editor에 포함된 의존 패키지도 시험 폴더에 복사할 수 있습니다. 이 저장소와 실행용 ZIP에는 해당 바이너리·패키지를 동봉하지 않습니다. 내려받은 패키지의 라이선스와 고지는 보관함에 유지합니다.
- **Codex CLI**: AI 기능에서 사용자가 지정하는 외부 실행 도구입니다. 바이너리·계정·인증 파일을 포함하지 않습니다.
- **.NET**: 실행용 배포본에는 Microsoft .NET 런타임과 해당 패키지의 `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT`가 포함됩니다.
- **MSTest / Microsoft.NET.Test.Sdk**: 자동 검사 의존성입니다. 버전과 의존성 목록은 테스트 프로젝트 및 `packages.lock.json`에 기록합니다.
- **화면 자산**: 생성 배경과 프로젝트의 구름 고양이 벡터를 사용합니다. 출처·변환 기록은 [자산 설명](src/UnityBridgeDesk.Desktop/Assets/README.md)에 있습니다.

UnityBridge Desk는 [MIT 라이선스](LICENSE)를 사용합니다. [표준 MIT 본문](https://opensource.org/license/mit)을 사용하며, 이 프로젝트의 라이선스가 외부 구성 요소의 라이선스를 대체하지는 않습니다.
