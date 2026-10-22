# InspectionView 인수인계 (2026-10-12)

다음 세션(Opus)이 바로 이어서 작업할 수 있도록 현재 상태, 작업 방식, 미완료 항목, 함정을 정리한다.
규칙/구조의 원본은 `CLAUDE.md` 이고, 이 문서는 "지금 어디까지 왔고 다음에 무엇을 할지" 다.

## 1. 한 줄 요약
OpenCV(OpenCvSharp4) 기반 Halcon/HDevelop 대체 툴. C# .NET 8 + net48 멀티타겟 Core/Script/Runtime, WPF IDE(App),
C++ Native(CUDA 옵션), C++/CLI Bridge(MFC 용), 카메라 벤더 플러그인. 저장소 `finallydo/InspectionView`, 브랜치 `main`.
확정 로드맵(연산자 → 2.5D → 카메라 → GPU → 노드 UI) 은 **모두 구현 + CI 통과** 상태. 남은 것은 §5.

## 2. 작업 방식 (반드시 유지)
- 사용자 PC: Windows + VS2022, `E:\InspectionView` 로 clone, Intel UHD 630 (CUDA 없음). 사용자는 UI 를 실기에서 확인하고 스크린샷/오류를 붙여 준다.
- 클라우드 세션은 Windows 빌드 불가 → **push 후 GitHub Actions(`.github/workflows/build.yml`, windows-latest) 로 빌드/테스트 확인**. 실패하면 로그(`get_job_logs failed_only`)를 읽고 고쳐 다시 push. "컴파일 확인 못 함" 으로 끝내지 않는다.
- 사용자 선호: 코드 질문에는 코드만 보여 주고, **수정은 명시적 요청이 있을 때만**. 다만 사용자가 "물어보지 말고 순서대로 진행" 이라고 한 로드맵 작업은 묻지 않고 진행했다.
- **브랜치 분리 금지 (2026-10-22)**: `main` 에 직접 커밋/푸시. 사용자도 PC 에서 main 에 커밋하므로 push 전 `git pull --rebase origin main`.
- 커밋: 영어 명령형 제목 + 본문, 끝에 `Co-Authored-By` / `Claude-Session` 줄. 모델 이름은 코드/커밋에 넣지 않는다.
- 테스트는 CI 에서 `dotnet test ... --no-build` 로 Core/Script/Runtime 세 프로젝트를 돌린다 (현재 Core 99, Script 23, Runtime 테스트 전부 통과).

## 3. 구조 요약 (자세한 것은 CLAUDE.md / README.md)
| 영역 | 위치 | 핵심 |
|---|---|---|
| 타입/메모리 | `Core/Types`, `Core/Memory` | `IconicObject` 참조 카운트(생성자 소유, 저장 시 AddRef, 끝나면 Release), `Image`(64B stride, Domain), `Region`(RLE+마스크 캐시), `XldContour`, `TupleValue`, `ObjectArray<T>`, `NccModel`, `CameraHandle` |
| 레지스트리 | `Core/Registry` | `[Operator]`/`[Input]`/`[Output]`/`[Backend]` 속성, `OperatorInvoker.Invoke` 가 에러 코드 반환, `ExecutionRecord` 에 요청/실제 백엔드 |
| 백엔드 | `Core/Backends` | `BackendSettings`(전역 + 연산자별 오버라이드, Gpu→Parallel→Cpu 폴백), `OpenClRuntime`(OpenCL.dll P/Invoke 로 GPU 탐지), `GpuRuntime`(CUDA 메모리만) |
| 연산자 | `Core/Operators/*` | IO, Segmentation, Regions, Images, Filters, Arithmetic, Morphology, Xld, Measure(MeasurePos/MeasurePairs), Matching(NCC 회전/스케일), Surface(2.5D), Camera |
| 카메라 | `Core/Cameras`, `plugins/` | `ICameraProvider`/`ICamera`/`CameraBase`, File·Simulation 내장, **GenTL 공급자**(`plugins/InspectionView.Cameras.GenTL`: .cti 로드 + 자체 GenICam 노드맵), FrameGrabbers 스텁 |
| 스크립트 | `Script/` | Lexer→Parser(AST)→Compiler(명령 리스트)→Interpreter, `ScriptEngine`(F5/F6/브레이크포인트), `Graph/GraphDocument`(노드 그래프 ↔ 스크립트) |
| 런타임 | `Runtime/`, `Bridge/` | `.ivjob` zip, `JobRunner`, `InspectionJob` C# API, `ivbridge.h` C API |
| IDE | `App/` | `MainViewModel`(100ms Tick 으로 UI 반영), `Editor/`, `Viewer/`(OpenTK GL), `Nodes/`(Nodify), `ViewModels/CameraViewModel` |

## 4. 최근 세션에서 한 일 (시간순)
1. 연산자 2차: EdgesSubPix/XLD 피팅/변환, MeasurePos, NCC 매칭, ListFiles.
2. 2.5D `Operators/Surface`: PhotometricStereo(Frankot-Chellappa 적분), FocusMeasure/DepthFromFocus(FocusOps 공유), ExtractLaserLine/MeasureProfileSheetOfLight/DisparityToHeight, PhaseShiftDecode, UnwrapPhase, ComposeIllumination.
3. 카메라 계층 + 프레임그래버 연산자 + IDE Camera 창 + 벤더 플러그인 4개 + AutoFocus.
4. GPU: OpenCL(UMat) 백엔드. `GpuFilterOperator`/`GpuBinaryOperator` 기반 클래스, 필터·산술·그레이 모폴로지·Rgb1ToGray 변환. **교훈**: Image 의 패딩 stride 를 그대로 감싼 UMat 은 OpenCV 가 큰 버퍼의 ROI 로 보고 경계 밖 패딩을 읽는다 → `FilterGpu` 는 `CopyTo` 로 연속 사본을 올린다.
5. 노드 UI: `GraphDocument`(스크립트가 원본, `// @node x,y` 로 위치 저장) + Nodify 편집기(Nodes 탭).
6. 3차 연산자: MeasurePairs, NCC 회전/스케일 탐색. **교훈**: 회전/스케일 템플릿은 픽셀 인덱스 중심 `((w-1)/2, (h-1)/2)` 기준으로 워프하고, 보고 좌표는 기존 관례대로 픽셀 경계 좌표(+0.5). 계단형 합성 테스트 이미지는 반 픽셀 정렬에 민감하므로 테스트용 모양은 살짝 흐린다.
7. (2026-10-22) **카메라 GenTL 통합**: 사용자는 Vieworks GigE 를 주로 쓰고 PC 의 `GENICAM_GENTL64_PATH` 에 Vieworks VZViewer / pylon / MVS / Spinnaker 프로듀서가 모두 있음. 벤더별 플러그인(Pylon/Spinnaker/Hikrobot)을 삭제하고 GenTL 공급자 하나로 통합.
   구성: `Native/GenTLApi.cs`(GenTL C API 델리게이트, LoadLibraryEx), `GenTLProducer`(TL/인터페이스/장치 열거), `GenTLCameraProvider`(프로듀서 병렬 열거, 시리얼로 중복 묶기, 제조사 프로듀서 우선 + 실패 시 다음 프로듀서), `GenTLCamera`(XML 로드 → NodeMap, 버퍼 announce/queue, NEW_BUFFER 이벤트), `GenICam/`(Formula, Nodes, NodeMap, DeviceDescription), `PixelFormats`(PFNC, Mono10/12Packed·p 언패킹, YUV422). 단위 테스트 55개(`tests/InspectionView.Cameras.GenTL.Tests`, CI 포함).
   **실기 미검증**: GenTL 호출 순서/상수, GigE 패킷 크기, 각 프로듀서의 URL 형식은 PC 에서 확인 필요. 문제가 나오면 출력 창 메시지(어느 GC 함수가 어떤 GC_ERR 로 실패했는지)를 받아 고친다.
8. (2026-10-22) 실기 피드백 반영: 라이브는 쌓인 버퍼 중 최신 것만 전달(`SkippedFrames`), 뷰어는 같은 크기/형식이면 텍스처를 재사용하고 이미지 메모리에서 바로 업로드(`ImageTextureSet.Update`, UnpackRowLength), 라이브 중 Camera 창은 읽기 전용 상태 값만 1초마다 갱신, Snap 은 활성 그래픽 창에 바로 표시, 변수창 더블클릭으로 재표시, Enum/Boolean 파라미터는 콤보박스.
9. (2026-10-22) 2차 피드백: 프로듀서는 Vieworks 폴더의 `GxGVTL` 이 선택됨 (VZ-2MG-M60C00, 1600x1200, 장치 ID `VZ-2MG-M60C00(00-21-49-06-8F-99)`). 라이브 중 파라미터 변경 시 `GCReadPort` 타임아웃 → "Device offline" → 닫은 뒤 장치가 안 보임, UI 가 심하게 멈춤.
   수정: Camera 창의 장치 I/O(열거/열기/닫기/파라미터/스냅/라이브 시작·정지)를 작업 큐(백그라운드)로 이동, 라이브 표시는 30ms 전용 타이머, 라이브 중에는 `CameraParameterInfo.IsStatus` 값만 갱신, `NodeMap.Batch()` 읽기 캐시(NoCache 제외, 쓰기 시 무효화), 포트가 offline/연속 타임아웃을 감지하면 이후 호출을 즉시 실패(`IsConnectionLost`), Open 은 직전 열거 결과로 바로 열고 실패 시 500ms → 2000ms 로 재탐색.
   3차 피드백(라이브 지연 그대로, 전체 버벅임): 프레임마다 UI 스레드에서 히스토그램/프로파일 전체 재계산 + 뷰어 범위 계산(GetPixel 25만 회) + Render 우선순위 라이브 타이머가 원인 후보 → 히스토그램은 초당 3회로 제한, 범위 계산은 행 포인터, 라이브 타이머는 Background 우선순위. 라이브 중 Info 에 수신/표시 fps, 수신→표시 지연, skipped/incomplete 표시. 라이브 중 잠긴 파라미터는 라이브를 멈추고 쓴 뒤 다시 시작.
   4차 측정(사용자 PC): received 58.4 fps, shown 17.9 fps, receive→show 21 ms, skipped/incomplete 0 → 카메라/네트워크는 정상, 표시 쪽이 18fps 로 끊김. 30ms DispatcherTimer 가 16.7ms 화면 주기와 어긋나는 문제로 보고 라이브 표시를 `CompositionTarget.Rendering`(vsync) 구동으로 변경.
   5차: vsync 구동 후에도 끊김 동일 → 히스토그램이 190만 픽셀을 GetPixel 로 초당 3회(UI 스레드, 회당 수십~100ms) 계산하던 것이 주 의심. 히스토그램/프로파일은 탭이 보일 때만, 8bit 는 포인터 + 30만 샘플, 막대는 경로 하나로. 정보 줄에 ui fps, render/upload ms 추가 (`Viewer/RenderStats`).
   **미해결 의심**: 1GbE 에서 2MP 60fps + 1500 바이트 패킷이면 링크가 거의 포화되어 제어 패킷이 타임아웃될 수 있음 → 점보 프레임 + `GevSCPSPacketSize`, 또는 `DeviceLinkThroughputLimit`/`GevSCPD` 로 확인 필요.

## 5. 다음 작업 우선순위 (2026-10-21 사용자와 합의)
사용자가 "다음에 뭐 해야 하지?" 라고 물으면 이 목록에서 아직 안 끝난 첫 항목을 안내한다. 기능 작업은 사용자가 명시적으로 요청할 때 시작한다.

**1순위 — PC 실기 확인 (사용자가 진행, 결과를 붙여 주면 수정)**
1. ~~솔루션 빌드 성공 확인~~ (2026-10-22 확인)
2. ~~§7 빠른 확인 스크립트: 뷰어, 상태줄 GPU, 타이밍 창 Gpu 백엔드(GaussFilter), 오버레이~~ (2026-10-22~23 확인)
3. ~~Camera 창: Simulation 카메라 스냅/라이브/파라미터~~ (확인)
4. ~~Nodes 탭: From Script/To Script/Run, 탭 전환 동기화, 노드 추가(Operators 더블클릭), 크기 기준 Auto Layout, 실행 노드 하이라이트/노드별 시간~~ (2026-10-23 확인)
5. ~~GenTL + Vieworks GigE 실기: 열기/스냅/라이브/파라미터, 라이브 끊김 수정~~ (2026-10-22 사용자 확인 완료)

**2순위 — 기능 개발 (위에서부터)**
1. 에지 기반 Shape 매칭 (`CreateShapeModel` / `FindShapeModel`): Halcon 대체의 핵심, NCC 는 조명 변화/가림에 약함
2. 카메라 캘리브레이션 (캘리브 판 → 내부/외부 파라미터, 왜곡 보정, 픽셀→mm)
3. 시트오브라이트 기하 캘리브레이션 (2번에 의존)
4. 뷰어 3D 높이맵 보기 (GL 작업, PC 확인 필수)
5. 후순위: CUDA 커널(사용자 PC 는 Intel UHD 630 이라 검증 불가), 영역 연산자 GPU 경로
- ~~끼워 넣을 정리: `MainViewModel.DisplayRequested` 미사용 경고(CS0067)~~ (제거)

## 5-1. 미완료 / 참고
- **GenTL 공급자는 실기 미검증** (§4-7). GenApi 미구현 부분: 레지스터 캐시/pInvalidator, 청크 데이터, 이벤트, pValueCopy, SmartFeature/ConfRom 등 구형 노드. 필요해지면 추가.
- 프레임그래버(CXP/Camera Link)는 사용자가 나중에 추가 예정. 그래버가 GenTL 프로듀서(.cti)를 제공하면 GenTL 공급자로 바로 열린다 (Euresys Coaxlink 등). 아니면 FrameGrabbers 스텁을 실제 SDK 구현으로 교체.
- 시트오브라이트 기하 캘리브레이션(현재는 `DisparityToHeight` 선형 변환만), 카메라 캘리브레이션, 에지 기반 shape 매칭.
- CUDA 커널(Native 에는 메모리/동기화만), Threshold/Connection 등 영역 연산자의 GPU 경로.
- 뷰어의 3D 높이맵 보기 (Float32 높이 이미지는 2D 로만 표시).
- 노드 UI 제한: 직선형 스크립트만 그래프로 표현. if/for 가 있으면 From Script 가 거부하고 이유를 상태줄에 표시.


## 6. 사용자 PC 빌드에서 알아둘 것
- `dotnet build InspectionView.sln` 은 C++ 프로젝트 때문에 실패 → VS 또는 `msbuild InspectionView.sln -restore -p:Configuration=Debug -p:Platform=x64`.
- **NuGet 복원에는 인터넷이 필요**하다. 최근 추가된 패키지는 `Nodify 6.6.0`(App). `NU1301: api.nuget.org 서비스 인덱스를 로드할 수 없습니다` 가 나오면 네트워크/프록시 문제이며, 복원이 안 되면 XAML 오류(`DockingManager`/`NodifyEditor` 태그 없음)가 연쇄로 나온다. 복원 한 번 성공하면 캐시(`%USERPROFILE%\.nuget\packages`)에서 오프라인 빌드가 된다.
- Bridge(C++/CLI) 는 "C++/CLI 지원" 구성 요소가 없으면 실패 → `-p:IVSkipBridge=true`.
- 카메라는 빌드 시 벤더 SDK 가 필요 없다. 실행 시 `GENICAM_GENTL64_PATH`(사용자 PC: Vieworks VZViewer\GenTL\Win64, pylon Runtime\x64, MVS Runtime\Win64_x64, Spinnaker cti64\vs2015)의 .cti 를 로드. 경로를 바꾸려면 `IV_GENTL_PATH`.
- 옛 벤더 플러그인 폴더(`plugins\InspectionView.Cameras.Pylon` 등)의 bin/obj 는 git 이 지우지 않으므로 PC 에 남아 있을 수 있다 (빌드에는 영향 없음, App 빌드가 출력 `Plugins\` 의 옛 DLL 은 지운다). 지저분하면 폴더째 삭제.
- 실기에서만 확인되는 것: OpenGL 뷰어(세 가지 필수 사항은 CLAUDE.md), Camera 창 Live, Nodes 탭, 상태줄의 `GPU: OpenCL: Intel(R) UHD Graphics 630`.

## 7. 빠른 확인 스크립트 (IDE 에서 F5)
```
Cam = OpenFramegrabber('Simulation', '', 'Pattern=Checker;TrueFocus=37')
Best = AutoFocus(Cam, 'FocusPosition', 0, 100)
Image = GrabImage(Cam)
Region = Threshold(Image, 128, 255)
Blobs = Connection(Region)
Feat = AreaCenter(Blobs)
Smooth = GaussFilter(Image, 2.0)          // 백엔드를 Gpu 로 두면 타이밍 창에 Gpu 표시
CloseFramegrabber(Cam)
```
