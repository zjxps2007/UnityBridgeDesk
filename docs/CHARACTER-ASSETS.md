# 아샤 여우 자산 제작

2026-09-20 · 내장 imagegen 스킬과 `image_gen` 편집 도구를 사용했다. 고양이 원본을 수정하지 않고 별도 투명 PNG를 생성한 뒤 프로젝트에 복사했다. 네트워크나 외부 이미지 파일 없이 WPF 리소스에서 불러온다.

- 기존 고양이 아라: `src/UnityBridgeDesk.Desktop/Assets/asha-sprites.png`. 기존 리소스 경로는 호환성을 위해 유지한다.
- 여우 아샤: `src/UnityBridgeDesk.Desktop/Assets/asha-fox-distinct-sprites.png`. 아이스 블루 장발·올라간 눈매·케이프 코트로 구분한 현재 앱 자산이다. [외형 구분·편집 프롬프트](ASHA-CHARACTER-DISTINCTION.md)
- 최초 아이스 블루 시트 `src/UnityBridgeDesk.Desktop/Assets/asha-fox-ice-sprites.png`는 제작 원본으로 보존한다. [색상 편집 기록](ASHA-ICE-BLUE.md)
- 최초 살구색 시트 `src/UnityBridgeDesk.Desktop/Assets/asha-fox-sprites.png`는 제작 원본으로 보존한다.
- 위쪽 4칸은 걷기, 아래쪽 4칸은 대기·눈 감기·수면·장난이다. 수면 프레임은 자산에 있지만 자율 탐색 중 자동 수면은 선택하지 않는다.
- 캐릭터의 발판 이동은 코드로 제어한다. 각 프레임의 귀·시선·꼬리·상체에는 작은 변형을 적용한다. 3D 모델이나 관절 모델로 변경한 것은 아니다.

## 최초 살구색 시트의 전체 생성 프롬프트

```text
Use case: precise-object-edit. Edit this existing 4 columns by 2 rows transparent chibi desktop mascot animation sprite sheet into its FOX companion ASHA, retaining precisely the same 4x2 grid, all eight full-body poses, proportions and consistent foot baseline within each cell. Image is reference/edit target. The cat sheet itself must remain unchanged; output a NEW fox character sheet. Distinct fox girl in the same clean hand-drawn anime chibi illustration style: long fluffy pale peach/cream hair with muted apricot streaks, tall pointed russet fox ears with dark brown tips and pale fluffy inner ears, ONE very large bushy orange-apricot fox tail with cream-white tip (not a skinny curled cat tail), amber eyes, playful sly inquisitive expression. Modest oversized cream sweater and small lavender scarf, small mint clasp, lavender shoes, no human skin exposure beyond face, no weapons. Make her visually harmonious beside the lavender-white cat while clearly fox by silhouette, ears and tail. All eight frames portray exactly the SAME character. Top row 4 right-facing walking cycle poses. Bottom row: front idle, front eyes closed blink, curled sleeping with bushy tail, front playful paw-up greeting. Keep complete ears and tail fully inside each individual equal-sized grid cell with modest transparent padding, do not cross cell edges. 4 columns x 2 rows only. True alpha transparent background with no checkerboard baked in, no background, no labels, no grid lines, no text, no glow, no shadows beneath feet. High-quality final in-game sprite atlas, not a presentation mockup.
```
