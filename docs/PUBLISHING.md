# GitHub에 올리기

## 소스와 실행 파일

- **저장소 Code**에는 `README.md`, `src/`, `tests/`, `docs/`, `build/`, 아이콘 원본과 프로젝트 설정을 올립니다.
- **Releases의 첨부 파일**에는 검사한 실행용 `UnityBridgeDesk-전달용-날짜시간.zip`을 올립니다.

실행용 ZIP과 소스 ZIP은 다릅니다. GitHub의 자동 Source code ZIP은 빌드 전 소스이며 실행 파일을 포함하지 않습니다. 사용자에게 실행용 첨부 파일을 안내하세요.

## 업로드할 소스 만들기

```powershell
pwsh -File ./build/Export-Source.ps1
```

명령은 `dist/` 아래 새 폴더와 ZIP을 만들고 경로를 출력합니다. 공개용 파일 목록만 복사하므로 개발 폴더의 캐시·실행 기록·과거 시안·배포 ZIP이 섞이지 않습니다. ZIP에는 `.gitignore`·`.gitattributes`·`.github/`도 포함합니다. 기존 파일과 폴더를 삭제하지 않습니다.

원하는 빈 위치를 지정할 수도 있습니다.

```powershell
pwsh -File ./build/Export-Source.ps1 -OutputDirectory 'C:/Repositories/UnityBridgeDesk-GitHub'
```

**출력 폴더 자체가 저장소 루트**입니다. 그 안의 `README.md`와 `src/`가 GitHub 첫 화면에 오도록 올립니다. 소스 ZIP 파일 자체만 Code에 올리거나, 작업 폴더 전체를 올리지 마세요.

## 첫 커밋 예시

GitHub에서 빈 저장소를 만든 뒤 정리한 소스 폴더 안에서 실행합니다. 마지막 두 줄의 URL은 본인 저장소 주소로 바꾸세요.

```powershell
git init -b main
git add .
git status --short
git commit -m "Prepare UnityBridge Desk source"
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push -u origin main
```

현재 정리 작업은 원격 저장소 생성·push를 수행하지 않습니다. 라이선스는 루트의 MIT `LICENSE`를 사용합니다.

## .gitignore에 포함한 제외 항목

빌드 폴더(`bin`, `obj`), `.cache`, `dist`, 테스트 산출물, 실행 기록과 복제본, 개인 설정·인증 파일, 로컬 설계 이력과 진단 기록을 제외합니다. `packages.lock.json`, `.csproj`, `.xaml`, 아이콘·배경 자산과 빌드 스크립트는 포함합니다.

`.gitignore`는 Git이 아직 추적하지 않는 파일에 적용됩니다. 이미 커밋된 개인 파일이나 웹에서 직접 선택한 업로드 파일을 정리해 주는 기능은 아닙니다. 처음에는 위 명령으로 만든 깨끗한 소스 폴더를 사용하는 편이 명확합니다.

## 실행본 공개

1. [개발 가이드](DEVELOPMENT.md)의 빌드·패키징·배포 검사를 수행합니다.
2. GitHub에서 릴리스를 작성하고 버전·변경 사항·시험 배포 범위를 적습니다.
3. 실행용 ZIP을 첨부하고 게시합니다. 개인 데이터 폴더는 첨부하지 않습니다.

Desk 앱의 버전과 비교 대상인 UnityBridge `0.2.0`·`0.2.1` 버전은 별개입니다. 앱 버전은 별도로 정하세요. 공개 서명·설치 프로그램·자동 업데이트는 현재 제공하지 않습니다.
