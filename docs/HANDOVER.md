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
- 커밋: 영어 명령형 제목 + 본문, 끝에 `Co-Authored-By` / `Claude-Session` 줄. 모델 이름은 코드/커밋에 넣지 않는다.
- 테스트는 CI 에서 `dotnet test ... --no-build` 로 Core/Script/Runtime 세 프로젝트를 돌린다 (현재 Core 99, Script 23, Runtime 테스트 전부 통과).

## 3. 구조 요약 (자세한 것은 CLAUDE.md / README.md)
| 영역 | 위치 | 핵심 |
|---|---|---|
| 타입/메모리 | `Core/Types`, `Core/Memory` | `IconicObject` 참조 카운트(생성자 소유, 저장 시 AddRef, 끝나면 Release), `Image`(64B stride, Domain), `Region`(RLE+마스크 캐시), `XldContour`, `TupleValue`, `ObjectArray<T>`, `NccModel`, `CameraHandle` |
| 레지스트리 | `Core/Registry` | `[Operator]`/`[Input]`/`[Output]`/`[Backend]` 속성, `OperatorInvoker.Invoke` 가 에러 코드 반환, `ExecutionRecord` 에 요청/실제 백엔드 |
| 백엔드 | `Core/Backends` | `BackendSettings`(전역 + 연산자별 오버라이드, Gpu→Parallel→Cpu 폴백), `OpenClRuntime`(OpenCL.dll P/Invoke 로 GPU 탐지), `GpuRuntime`(CUDA 메모리만) |
| 연산자 | `Core/Operators/*` | IO, Segmentation, Regions, Images, Filters, Arithmetic, Morphology, Xld, Measure(MeasurePos/MeasurePairs), Matching(NCC 회전/스케일), Surface(2.5D), Camera |
| 카메라 | `Core/Cameras`, `plugins/` | `ICameraProvider`/`ICamera`/`CameraBase`, File·Simulation 내장, Pylon/Spinnaker/Hikrobot 플러그인(SDK 조건부 컴파일), FrameGrabbers 스텁 |
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

## 5. 다음 작업 우선순위 (2026-10-21 사용자와 합의)
사용자가 "다음에 뭐 해야 하지?" 라고 물으면 이 목록에서 아직 안 끝난 첫 항목을 안내한다. 기능 작업은 사용자가 명시적으로 요청할 때 시작한다.

**1순위 — PC 실기 확인 (사용자가 진행, 결과를 붙여 주면 수정)**
1. 솔루션 빌드 성공 확인 (경고/오류 목록, 플러그인 `SDK true/false` 출력)
2. §7 빠른 확인 스크립트 F5: 뷰어 표시, 오버레이, 변수창 썸네일, 타이밍 창의 Gpu 백엔드, 상태줄 GPU 표시
3. Camera 창: Simulation 카메라 스냅/라이브/파라미터
4. Nodes 탭: From Script → 노드 이동 → To Script → Run
5. pylon / Spinnaker 실제 카메라: 열기, 스냅, 라이브, 파라미터 읽기/쓰기 (Hikrobot 은 SDK 설치 후)

**2순위 — 기능 개발 (위에서부터)**
1. 에지 기반 Shape 매칭 (`CreateShapeModel` / `FindShapeModel`): Halcon 대체의 핵심, NCC 는 조명 변화/가림에 약함
2. 카메라 캘리브레이션 (캘리브 판 → 내부/외부 파라미터, 왜곡 보정, 픽셀→mm)
3. 시트오브라이트 기하 캘리브레이션 (2번에 의존)
4. 뷰어 3D 높이맵 보기 (GL 작업, PC 확인 필수)
5. 후순위: CUDA 커널(사용자 PC 는 Intel UHD 630 이라 검증 불가), 영역 연산자 GPU 경로
- 끼워 넣을 정리: `MainViewModel.DisplayRequested` 미사용 경고(CS0067)

## 5-1. 미완료 / 참고
- **벤더 카메라 플러그인은 미검증**: SDK 없이 작성. 사용자 PC 에는 pylon 과 Spinnaker 가 설치되어 있어 (`SDK true` 로 인식됨) 실제 컴파일이 되며, 첫 빌드에서 나온 오류(이름 충돌, `ManagedCameraList` using, `GetNode<INode>`)는 수정했다. 추가 컴파일/런타임 오류는 사용자가 붙여 주는 VS 오류 목록으로 고친다 (CI 는 SDK 가 없어 스텁만 컴파일한다).
- Hikrobot MVS 는 사용자 PC 에 SDK 가 없어(`SDK false`) 아직 실제 컴파일된 적이 없다.
- 시트오브라이트 기하 캘리브레이션(현재는 `DisparityToHeight` 선형 변환만), 카메라 캘리브레이션, 에지 기반 shape 매칭.
- CUDA 커널(Native 에는 메모리/동기화만), Threshold/Connection 등 영역 연산자의 GPU 경로.
- 뷰어의 3D 높이맵 보기 (Float32 높이 이미지는 2D 로만 표시).
- 노드 UI 제한: 직선형 스크립트만 그래프로 표현. if/for 가 있으면 From Script 가 거부하고 이유를 상태줄에 표시.
- App 의 `MainViewModel.DisplayRequested` 미사용 경고(CS0067) 정리.

## 6. 사용자 PC 빌드에서 알아둘 것
- `dotnet build InspectionView.sln` 은 C++ 프로젝트 때문에 실패 → VS 또는 `msbuild InspectionView.sln -restore -p:Configuration=Debug -p:Platform=x64`.
- **NuGet 복원에는 인터넷이 필요**하다. 최근 추가된 패키지는 `Nodify 6.6.0`(App). `NU1301: api.nuget.org 서비스 인덱스를 로드할 수 없습니다` 가 나오면 네트워크/프록시 문제이며, 복원이 안 되면 XAML 오류(`DockingManager`/`NodifyEditor` 태그 없음)가 연쇄로 나온다. 복원 한 번 성공하면 캐시(`%USERPROFILE%\.nuget\packages`)에서 오프라인 빌드가 된다.
- Bridge(C++/CLI) 는 "C++/CLI 지원" 구성 요소가 없으면 실패 → `-p:IVSkipBridge=true`.
- 벤더 SDK 경로는 자동 탐색(`PYLON_DEV_DIR`, `MVCAM_SDK_PATH`, `Program Files\Teledyne|FLIR Systems\Spinnaker`). 빌드 출력의 `InspectionView.Cameras.X: SDK true/false (경로)` 로 확인. 다른 경로면 `-p:IVPylonAssembly=...`, `-p:IVSpinnakerAssembly=...`, `-p:IVMvsAssembly=...`.
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
