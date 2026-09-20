# 아샤의 아이스 블루 색상

> 최초 색상 변경의 기록이다. 이후 머리·눈매·의상을 구분한 현재 자산은 [외형 구분 문서](ASHA-CHARACTER-DISTINCTION.md)를 따른다. 아래 앱 화면 이미지는 최신 자산으로 갱신한다.

2026-09-20 · 여우 아샤의 색상을 파스텔 아이스 블루로 변경한다. 얼굴·헤어스타일·큰 여우 꼬리·8개 포즈·행동·크기는 유지한다. 고양이 아라의 자산과 설정은 변경하지 않는다.

머리와 꼬리는 밝은 아이스 블루, 그림자는 파우더 블루, 꼬리 끝과 하이라이트는 진주빛 흰색을 사용한다. 눈은 부드러운 블루 틸로 맞추고 귀 끝은 채도가 낮은 청회색으로 구분한다. 피부와 옷의 밝은 바탕, 작은 홍조를 남겨 전체에 파란 필터를 씌운 인상을 피한다. 어두운 윤곽선은 작은 크기에서도 표정과 실루엣을 읽을 수 있게 유지한다.

내장 imagegen 스킬과 image_gen 편집 도구를 사용한다. 입력은 기존 `Assets/asha-fox-sprites.png`, 새 출력은 `Assets/asha-fox-ice-sprites.png`이다. 이전 살구색 원본은 보존하며 앱은 새 자산을 사용한다. 외부 API·CLI 폴백을 사용하지 않는다.

[디자인 작업 기준](../design/UI-DESIGN-WORKFLOW.md)과 [캐릭터 설계](CHARACTERS.md)의 기존 그림체·명확한 종별 실루엣·정지 설정 보존 원칙을 적용한다. 기존 작업에서 확인한 frontend-design의 사용자 지정 색상 존중, UI/UX Pro Max의 작은 화면 가독성 원칙을 유지한다. 이번 변경은 색상 자산에 한정하며 화면 배치나 장식 효과는 추가하지 않는다.

## 적용과 확인

저장 파일은 [asha-fox-ice-sprites.png](../src/UnityBridgeDesk.Desktop/Assets/asha-fox-ice-sprites.png)이다. 1774×887 RGBA 이미지이며 실제 투명도를 포함한다. WPF 내장 리소스와 여우의 자산 선택을 이 파일로 연결했다. 아라의 리소스와 두 캐릭터의 이동 코드는 변경하지 않았다.

별도 상태 폴더에서 실제 WPF 준비 화면과 캐릭터 설정을 1440×850·960×660으로 렌더링했다. 작업면의 96 DIP 캐릭터와 설정의 64 DIP 미리보기에서 색상·표정·꼬리와 잘림 여부를 확인했다. 같은 미리보기에서 여우를 놓아 중력에 따라 낙하하고 발판에 착지하는 흐름도 확인했다. Unity 벤치를 실행하거나 사용자의 실행 상태를 변경하지 않았다. 이번 색상 변경에서 전체 단위 검사를 다시 실행하지는 않았다.

![아라와 아이스 블루 아샤의 실제 설정 화면](images/ara-asha-settings.png)

## 편집 프롬프트

```text
Use case: precise-object-edit. This image is the EDIT TARGET: an existing 4-column by 2-row, 8-frame transparent animation atlas of the fox girl Asha. Make a COLOR-ONLY retouch to soft pastel ICE BLUE. Preserve exactly the same character identity, face shape, sly half-lidded curious/playful expression, long fluffy hairstyle, tall fox ears, one large bushy fox tail, outfit, linework style, all eight distinct poses, per-cell position and scale, equal grid, padding, shoe ground alignment, and transparent canvas. Change peach/apricot/orange hair and fox fur to very pale icy blue (#DCEEF7) with powder-blue shadows (#B9D8EB), snowy pearl-white highlights and tail tip. Ear tips become muted cool slate-blue rather than brown. Change amber irises to soft blue-teal (#83B5CF), retaining dark pupils and clear expressions. Keep ivory sweater and skin naturally warm-white; subtle pink inner ears and blush remain. Scarf and shoes become muted periwinkle/ice-blue harmonizing with pastel lilac UI; keep the tiny mint clasp. Keep clear dark muted blue-plum outlines for readability at 96px. Delicate low-saturation pastel, not saturated cyan, not gray monochrome. NO new accessories, no frost crystals, no snowflakes, no glow, no scenery. Do NOT redesign or reposition any poses or crop ears, feet or tail. Exactly four equal columns and two equal rows: top four walking cycle frames; bottom idle, blink, sleeping, playful paw-up. Preserve true alpha transparency, no solid white or checkerboard background, no text or grid lines. Output the finished production sprite atlas at the same 2:1 aspect ratio.
```
