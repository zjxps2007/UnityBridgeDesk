# GitHub 업로드 · v0.4.0

현재 앱 버전은 **v0.4.0 정식**입니다. 비교 대상 UnityBridge의 버전과 구분합니다.

## 어디에 어떤 파일을 올리나요?

| 대상 | 파일 / 내용 |
|---|---|
| 저장소 Code | 소스 ZIP을 풀었을 때 나오는 폴더의 **내용물**. README.md, CHANGELOG.md, src, tests, docs, build, .gitignore, .github 등이 저장소 루트에 오도록 반영 |
| Releases → Assets | `UnityBridgeDesk-v0.4.0-win-x64.zip`, `SHA256SUMS.txt` |
| 릴리스 태그 | `v0.4.0` |
| 릴리스 제목 | `UnityBridge Desk v0.4.0 · exec·부하 안정성·그래프` |
| 릴리스 본문 | [v0.4.0 본문](releases/v0.4.0.md)의 첫 제목 아래 내용 |
| GitHub Desktop Summary / Description | [커밋 문구](releases/v0.4.0-commit.md) |

**실행용 ZIP은 Releases에 첨부**합니다. 소스 ZIP 자체나 실행용 ZIP을 Code에 넣지 마세요. GitHub가 자동 생성하는 Source code ZIP에는 실행 파일이 없습니다. EXE 하나만 전달하면 실행에 필요한 app 폴더가 빠집니다.

## 소스 반영

1. 기존 저장소가 있으면 현재 수정 내용을 확인하고, 준비한 소스 폴더의 변경 사항을 반영합니다. 저장소의 `.git` 폴더를 덮어쓰거나 삭제하지 않습니다.
2. GitHub Desktop에서 README·소스·문서·버전 변경을 확인합니다. 개인 설정·캐시·결과 파일이 없는지 확인합니다.
3. 제공한 Summary·Description으로 커밋하고 push합니다. 저장소가 아직 없다면 먼저 빈 저장소를 만들고 이 소스 폴더를 연결합니다.
4. 업로드 후 Actions의 Windows build and tests 결과를 확인합니다. 로컬 검사 통과와 실제 GitHub Actions 실행은 별개입니다.

정리한 소스 ZIP에는 `.gitignore`·`.gitattributes`·`.github/`와 MIT 라이선스가 포함됩니다. 숨김 파일을 빼고 복사하지 않도록 주의하세요.

## 릴리스 작성

1. 소스가 올라간 커밋을 대상으로 **새 릴리스**를 작성합니다.
2. 태그 `v0.4.0`, 제목 `UnityBridge Desk v0.4.0 · exec·부하 안정성·그래프`을 입력합니다.
3. [릴리스 본문](releases/v0.4.0.md)을 붙여넣습니다.
4. 실행용 ZIP과 `SHA256SUMS.txt`를 첨부합니다.
5. **Pre-release는 선택하지 않습니다.** 정식 v0.4.0으로 게시합니다.

앱은 정식 배포이며 기본 측정 설정과 분석은 예비 기술 통계입니다. README·릴리스 본문에 확인한 범위와 한계를 유지하세요. 공개 서명·설치 프로그램·자동 업데이트는 현재 제공하지 않습니다.

이 안내와 파일 준비는 GitHub 저장소 생성·push·릴리스 게시를 자동으로 수행하지 않습니다.

## 다시 빌드하기

버전 기준은 `Directory.Build.props`입니다. 앱·Worker·배포 명세와 실행기 버전을 여기에 맞춥니다.

```powershell
pwsh -File ./build/Verify.ps1 -Restore
pwsh -File ./build/Publish-Desk.ps1
# Publish가 출력한 실제 폴더 경로를 아래에 사용합니다.
pwsh -File ./build/Package-Desk.ps1 -BundlePath 'C:/.../UnityBridgeDesk-win-x64-날짜시간'
pwsh -File ./build/Verify-Package.ps1 -PackagePath './dist/UnityBridgeDesk-v0.4.0-win-x64'
pwsh -File ./build/Export-Source.ps1
```

같은 버전의 패키지 폴더나 ZIP이 이미 있으면 덮어쓰지 않습니다. 기존 배포본을 별도로 보관하고 다음 버전을 지정하세요. 소스 내보내기는 새 폴더만 만들며 기존 저장소를 삭제하지 않습니다. 자세한 빌드 환경은 [개발 가이드](DEVELOPMENT.md)에 있습니다.

실행용 ZIP의 SHA-256은 다음처럼 확인할 수 있습니다. 받은 `SHA256SUMS.txt`의 같은 파일 이름과 대조하세요.

```powershell
Get-FileHash -LiteralPath './UnityBridgeDesk-v0.4.0-win-x64.zip' -Algorithm SHA256
```

## 공개 소스에서 제외하는 자료

빌드 결과·배포 ZIP·.cache·TestResults, 개인 설정·인증 파일, 실제 결과와 로그·dump, 내려받은 외부 릴리스, VM 이미지·ISO를 제외합니다. `.gitignore`와 `build/Export-Source.ps1`의 공개 파일 목록을 함께 사용합니다.

`.gitignore`는 이미 추적한 파일이나 웹에서 직접 선택한 파일을 지워 주지 않습니다. 준비한 소스 폴더를 기준으로 변경 목록을 확인하세요.

