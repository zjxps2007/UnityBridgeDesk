# 테마·캐릭터 제작 기록

## 현재 캐릭터 — 아라와 아샤 · 2026-09-20

기존 고양이의 이름은 아라로 바꾼다. 그림 원본과 `asha-sprites.png` 경로를 유지한다. 여우 아샤의 현재 자산은 `asha-fox-distinct-sprites.png`이며 둘 중 하나 또는 둘 다 표시할 수 있다. 같은 그림체 안에서 아샤는 파스텔 아이스 블루의 매끈한 장발·옆으로 넘긴 앞머리·올라간 눈매·케이프 코트·큰 흰 끝 꼬리로 구별한다. 최초 살구색과 첫 아이스 블루 시트는 제작 원본으로 보존한다. 아래의 2026-09-19 아샤는 당시 고양이의 이름이다. [사용법](CHARACTERS.md) · [여우 자산·전체 프롬프트](CHARACTER-ASSETS.md) · [외형 구분](ASHA-CHARACTER-DISTINCTION.md)

## 현재 캐릭터 — 아샤 · 2026-09-19

현재 마스코트는 사용자가 이름을 정한 ‘아샤’이다. 장발 고양이 SD 외형을 기반으로 대기·걷기·눈 깜빡임·졸기·장난 프레임을 내장 이미지 생성 도구로 제작했다. `Assets/asha-sprites.png`를 WPF 리소스로 포함하고 `DeskMascot`이 표시한다. `AshaCompanion`이 앱의 빈 공간에서 자율 행동과 이동을 제어한다. 기준 외형은 `Assets/asha-reference.png`에 보존한다. [사용법·프롬프트·제작 방식](ASHA.md)

이전 PNG·벡터 시안은 아래 제작 기록으로 남긴다. 앱 아이콘은 기존 구름 고양이를 유지한다. 아래의 화면 개편에서 풍경과 일반 화면 선택을 연결했다.

## 현재 화면 — 풍경 3종과 일반 화면

2026-09-19 내장 Imagegen 스킬과 image_gen 편집 도구로 기존 세 풍경의 그림체를 가볍게 리터칭했다. 오른쪽 역·구름 형태·구도·테마 색을 보존하고 선과 회화 질감만 다듬었다. 외부 작품이나 모델을 다운로드하지 않았다.

- [연보라](../src/UnityBridgeDesk.Desktop/Assets/cloud-station-lofi.png)
- [로즈](../src/UnityBridgeDesk.Desktop/Assets/cloud-station-rose-lofi.png)
- [민트](../src/UnityBridgeDesk.Desktop/Assets/cloud-station-mint-lofi.png)

세 출력 원본을 프로젝트에 복사해 WPF 리소스에 포함했다. 별도의 이미지 설치나 네트워크 연결 없이 사용한다. 앱은 상단에 풍경 일부를 표시하고 텍스트 작업면은 불투명하게 유지한다. 일반 화면은 이미지를 숨기고 중립 색면을 사용한다. 화면 설정은 아샤 표시와 독립적이다. 원본 그림은 덮어쓰지 않고 제작 자료로 보존한다.

[전체 입력·프롬프트 기록](../design/landscape-retouch-prompts.json) · [화면 구성](UI-NAVIGATION.md)

## 이전 작업 화면 — 2026-09-18

당시 속도 벤치 화면은 전체 풍경 배경을 사용하지 않았다. 연보라·로즈·민트 색면과 WPF Path로 그린 작은 구름 곡선으로 정체성을 유지한다. 아래 배경 제작 기록과 기존 파일은 과거 화면의 자료로 보존한다.

이전 마스코트는 WPF에 직접 그린 2D 벡터였다. 머리가 대부분을 차지하는 구름 고양이에 반쯤 뜬 눈, 작은 송곳니, 삐딱한 앞머리와 짧은 팔다리를 적용했다. 당시에는 눈·고개·몸·귀·꼬리를 따로 움직였고 이미지 생성 도구나 외부 모델을 사용하지 않았다. 현재 컨트롤은 위의 아샤 프레임 방식으로 교체했다.

![구름 고양이 동작 미리보기](images/mascot-motion.gif)

미리보기는 실제 WPF 컨트롤을 6초간 촬영한 것이다. 1초와 3초 지점에 클릭 반응을 호출했다. [동작 범위와 정지 조건](MOTION.md)

## 이전 사람형 SD 일러스트 — 제작 기록 보존

이전 캐릭터는 내장 image_gen 도구로 생성한 원본 SD 일러스트이며 외부 API·CLI 폴백은 사용하지 않았다. 사용자가 제시한 [Little LUMI Model](https://store.steampowered.com/app/5075020/_Little_LUMI_Model/?l=koreana)은 작은 데스크톱 동반자의 역할만 참고했다. 해당 게임의 이미지나 모델을 복사하지 않았다.

저장 파일: [desk-companion-sd.png](../src/UnityBridgeDesk.Desktop/Assets/desk-companion-sd.png). 투명 배경, 은보라 머리, 고양이 귀 후드와 구름 머리핀을 가진 이전 캐릭터이다. 현재 화면은 위의 벡터 마스코트로 교체했고 이 PNG는 제작 기록으로 보존한다.

사용한 전체 프롬프트:

```text
Use case: stylized-concept. Asset type: transparent desktop companion illustration for UnityBridge Desk, a pastel lilac benchmarking desktop application. Create ONE original Japanese subculture SD chibi assistant, not an existing character. Full body in a relaxed seated pose, legs tucked to one side, facing mostly front with a slight tilt; one small hand lifted in a friendly greeting. Large expressive lavender eyes, short fluffy silver-lilac bob hair, small cat-ear hood integrated into an oversized ivory and muted lavender tech hoodie, simple charcoal shorts and lilac shoes. A small cloud-shaped hair clip connects to the app's cloud-cat identity. Two-and-a-half-head-tall proportions. Crisp confident dark plum outer line, clean limited cel-shaded color areas, tidy professional game sticker quality, cute but restrained. At 88 px high the silhouette and face must remain clear. Flat colors ivory #FFFEFC, lilac #DCCCF1, plum #584278, subtle blush #F1CBDC. Exactly one character centered, occupies most of square canvas with complete ears, hands, feet visible. Real transparent alpha background, no white rectangle, no background scene, no typography, no logo, no sparkle, no glow, no cast shadow, no extra objects, no alternate poses. This is an original illustration; do not reproduce any named character or outfit.
```

## 이전 풍경 배경 제작 기록

2026-09-17 내장 image_gen 도구로 기존 프로젝트 배경을 편집했다. 외부 API나 CLI 폴백은 사용하지 않았다. 1차로 로즈·민트의 색감과 빛을 만들고, 사용자 요청에 따라 2차로 구름 모양을 변주했다. 두 테마 모두 기존 역과 파스텔 풍경의 구도를 기준으로 한다.

최종 파일은 원본 출력 그대로 복사한 1586 × 992 PNG이며 WPF 리소스로 앱에 포함한다. 인터넷이나 별도 이미지 폴더가 필요 없다. 기존 연보라 배경은 cloud-station.png를 사용한다.

- 로즈: [cloud-station-rose.png](../src/UnityBridgeDesk.Desktop/Assets/cloud-station-rose.png) — 둥글게 뭉친 구름·분홍빛 새벽.
- 민트: [cloud-station-mint.png](../src/UnityBridgeDesk.Desktop/Assets/cloud-station-mint.png) — 얇고 길게 흐르는 구름·맑은 아침.

아래는 사용한 프롬프트 전체이다.

이미지 링크는 소스 폴더 기준이다. 실행용 ZIP에서는 이미지가 앱 리소스에 내장되므로 별도 이미지 파일을 설치할 필요 없다.

## 1차 색감 편집

### rose

```text
Use case: lighting-weather. Asset type: landscape wallpaper for a pastel desktop application, wide 16:10. Edit the provided cloud-station illustration into its ROSE theme companion. Keep the same delicate anime background-art style, floating railway station on the far right, hanging plants and cloud-sea world; keep the central and left sky quiet so app panels can sit over it. Change the atmosphere to a soft rose-pink and peach dawn: pale blush sky, warm cream light, airy coral-pink clouds, muted mauve shadows, soft warm haze, flowers on the station in dusty pink. Give the clouds a distinct dawn formation while keeping the scene recognizable. Smooth light pastel values with crisp architectural lines. Full image wallpaper, not a UI mockup. No people, no mascot, no text, logos, borders, sparkles, glow effects, or extra decorative objects. Match reference framing, output one high-quality wide image.
```

### mint

```text
Use case: lighting-weather. Asset type: landscape wallpaper for a pastel desktop application, wide 16:10. Edit the provided cloud-station illustration into its MINT theme companion. Keep the same delicate anime background-art style, floating railway station on the far right, hanging plants and cloud-sea world; keep the central and left sky quiet so app panels can sit over it. Change the atmosphere to a clear cool mint morning: pale seafoam and powder-blue sky, luminous ivory clouds, subtle sage-green foliage, distant blue-green hills, cool gentle daylight and light atmospheric haze. Give the clouds a distinct open morning formation while keeping the scene recognizable. Smooth light pastel values with crisp architectural lines. Full image wallpaper, not a UI mockup. No people, no mascot, no text, logos, borders, sparkles, glow effects, or extra decorative objects. Match reference framing, output one high-quality wide image.
```

## 2차 구름 형태 편집 — 최종

### rose

```text
Use case: lighting-weather. Edit only the CLOUD SHAPES in this rose pastel fantasy railway-station wallpaper. Keep the existing rose-peach palette, light direction, crescent moon, composition, camera, right-hand floating railway station, its foliage, rails and all architectural details unchanged. Give this theme a distinct but subtle cloud pattern: soft rounded billowing cumulus clusters with larger pillowy lobes, a few separate rounded cloud islands through the lower half, gentle scalloped silhouettes along the edges. Replace the existing speckled high cloud field with a few softly rounded cloudlets, preserving ample quiet open sky in the center. Clouds should still feel natural and delicately hand-painted, with the same restrained anime-background style and brightness. The change must be visible as different cloud silhouettes, not a recolor. No extra objects, no figures, no text, no glow/sparkles. Preserve the source dimensions and framing. One final wallpaper.
```

### mint

```text
Use case: lighting-weather. Edit only the CLOUD SHAPES in this mint morning fantasy railway-station wallpaper. Keep the existing pale mint-powder-blue palette, light direction, composition, camera, right-hand floating railway station, its foliage, rails and all architectural details unchanged. Give this theme a distinct but subtle cloud pattern: thin elongated wind-swept cirrus wisps in the upper sky and long soft stratocumulus ribbons through the lower half, with more horizontal breaks and airy gaps. Reduce the tall rounded cloud tower above and behind the station into softer low horizontal layers, without changing the station or mountains. Preserve ample quiet open sky in the center. Clouds should still feel natural and delicately hand-painted, with the same restrained anime-background style and brightness. The change must be visible as different cloud silhouettes, not a recolor. No extra objects, no figures, no text, no glow/sparkles. Preserve the source dimensions and framing. One final wallpaper.
```
