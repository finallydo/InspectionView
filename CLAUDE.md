# InspectionView

OpenCV 기반 Halcon(HDevelop) 대체 머신비전 개발 툴. 이 파일은 Claude Code 가 매 세션 시작 시 읽는 프로젝트 규칙이다.

## 작업 방식 (중요)
- 사용자가 **코드 관련 질문**을 하면 코드를 수정하지 말고 답변/코드 제시만 한다. 수정은 **명시적인 요청이 있을 때만** 진행한다.
- 클라우드 세션에서는 Windows 빌드가 불가능하다. push 후 GitHub Actions (`.github/workflows/build.yml`, windows-latest) 결과로 빌드/테스트를 확인하고, 실패하면 스스로 고쳐서 다시 push 한다. "컴파일 확인 못 함" 상태로 끝내지 않는다.
- 커밋 메시지는 영어 명령형 한 줄 제목 + 필요 시 본문. 코드 주석/문서는 한국어 허용.
- 사용자는 VS2022 가 설치된 Windows PC 에서 `E:\InspectionView` 로 clone 해 확인한다. 생성 파일/도구는 VS2022 + .NET 8 SDK 로 열리는 형태여야 한다.

## 빌드 / 테스트
```
msbuild InspectionView.sln -restore -p:Configuration=Debug -p:Platform=x64
dotnet test tests/InspectionView.Core.Tests -c Debug -p:Platform=x64
```
- `dotnet build InspectionView.sln` 은 C++ 프로젝트(InspectionView.Native) 때문에 실패한다. 솔루션 전체는 msbuild, C# 프로젝트 단위는 `dotnet build <csproj>` 를 쓴다.
- 플랫폼은 **x64 고정** (Directory.Build.props). AnyCPU / x86 구성을 추가하지 않는다.

## 솔루션 구조
| 프로젝트 | 타겟 | 역할 |
|---|---|---|
| src/InspectionView.Core | net8.0; net48 | 데이터 타입(Image/Region/XLD/Tuple/ObjectArray), 메모리, 백엔드, 연산자 레지스트리, 기본 연산자 |
| src/InspectionView.Native | C++ DLL (x64) | GPU 백엔드 (CUDA 옵션, `IVN_WITH_CUDA`). C API 는 `include/ivnative.h`, C# 선언은 `Core/Backends/GpuRuntime.cs` 와 일치 유지 |
| src/InspectionView.Script | net8.0; net48 | 스크립트 AST / 파서 / 인터프리터 |
| src/InspectionView.Runtime | net8.0; net48 | Job 패키지(.ivjob zip: manifest.json/script.ivs/params/roi/models) `JobPackage`, `JobDocument`, 동기 실행기 `JobRunner`(터미널 바인딩, 에러 코드), 검사 프로그램 API `InspectionJob` |
| src/InspectionView.Bridge | C++/CLI DLL (net48) | MFC 용 C API (`ivbridge.h`: ivb_job_load/set_image/run/get_*). "C++/CLI 지원" 구성 요소 필요. CI 는 best-effort, 솔루션 빌드 시 `-p:IVSkipBridge=true` 로 건너뛸 수 있음 |
| plugins/InspectionView.Cameras.* | net8.0 | 카메라 벤더 플러그인 (Pylon, Spinnaker, Hikrobot, FrameGrabbers 스텁). SDK 가 빌드 PC 에 있을 때만 실제 코드가 컴파일되고(`IV_HAS_*`), 없으면 "사용 불가" 공급자. App 빌드 후 `Plugins/` 로 복사 |
| src/InspectionView.App | net8.0-windows WPF | IDE: `MainViewModel`(엔진 소유, 이벤트를 UI 타이머로 모아 반영), `Editor/`(하이라이트, 브레이크포인트 여백, 실행 줄/오류 렌더러, 자동완성), AvalonDock 레이아웃. 이후 OpenTK GLWpfControl, Nodify |
| tests/InspectionView.Core.Tests | net8.0 xunit | Core 단위 테스트 |
| tests/InspectionView.Script.Tests | net8.0 xunit | 렉서/파서/컴파일러/엔진 테스트 |
| tests/InspectionView.Runtime.Tests | net8.0 xunit | 패키지 입출력, 러너, 터미널, 파사드 테스트 |

## 코딩 규칙
- C# 12, nullable enable, ImplicitUsings 비활성 (net48 호환을 위해 using 명시).
- Core/Script/Runtime 은 **net48 에서도 컴파일**되어야 한다: `required` 멤버, `System.Text.Json` 의존, net5+ 전용 API 사용 금지. `init` 은 폴리필 있음.
- 연산자 이름/파라미터/스크립트 식별자는 **PascalCase**. 파라미터 순서는 입력 먼저, 반환값은 출력. 출력이 여럿이면 결과 객체(`ResultObject`) 로 반환.
- 아이코닉 객체는 `IconicObject` 상속 (참조 카운트). 생성자는 소유권을 가지며, 전달/보관 시 `AddRef()`, 끝나면 `Release()`/`Dispose()`. 새로 만든 객체를 `ObjectArray` 에 넣을 때는 `AddOwned`.
- 이미지 픽셀 접근은 `Image.GetRow<T>` / `MatInterop.AsMat` (복사 없음). `Mat` 을 Image 보다 오래 살려두지 않는다.
- Region 은 불변. 마스크는 `ToMask()` 캐시를 쓰고 반환 Mat 을 수정/Dispose 하지 않는다.
- 이미지→이미지 연산자는 `Operators/Common/ImageToImageOperator`(입력 `Image`, 출력 `ImageResult`, Cpu 백엔드 상속)를 상속하고 `ImageOps.Filter(src, (s, d) => Cv2.X(s, d, ...))` 로 구현한다 (결과는 입력 Domain 을 물려받음). Region 모폴로지/마스크 연산은 `RegionOps` 헬퍼(로컬 마스크 ↔ Region). 기반 클래스의 입력은 Order 로 순서를 고정한다.
- 새 연산자: `[Operator("Name", Category=...)]` 클래스 + `[Input]`/`[Output]` 프로퍼티 + `[Backend(BackendKind.Cpu|Parallel|Gpu)] void Run(OperatorContext ctx)`. 입력 검증은 `Validate()` 에서 `VisionException(ErrorCode)` 로. 최소 Cpu 백엔드는 항상 제공.
- OpenCV 우선. 병목이면 `Parallel.For`(ctx.CreateParallelOptions()) 또는 직접 구현 허용.
- WPF: 엔진 이벤트는 실행 스레드에서 오므로 뷰모델은 변경을 모아 두고 100ms DispatcherTimer(`MainViewModel.Tick`)에서 반영한다. UI 코드는 아이코닉 객체를 AddRef/Release 하지 않는다 (VariableTable 소유). 예외: `Viewer/ImageViewer` 는 표시 중인 Image 를 AddRef 해 소유한다.
- 뷰어(`App/Viewer`): OpenTK 4 + GLWpfControl, GL 3.3. 실기(Intel UHD 630 + AvalonDock)에서 확인된 필수 사항 세 가지: (1) AvalonDock 이 창 내용을 Unloaded/Loaded 시키므로 GLWpfControl 은 Loaded 마다 새로 만들고 GL 자원을 다시 올린다, (2) 다시 그리기는 CompositionTarget.Rendering 에서 직접 InvalidateVisual 한다, (3) 프레임 끝에 GL.Flush+GL.Finish 가 없으면 D3D 공유 표면에 첫 프레임만 올라온다. 모든 GL 호출은 Render 콜백 안에서만 (업로드/삭제는 pending 큐로 미룸). 이미지는 `ImageTextureSet`(≤4096 타일 + 밉맵), 오버레이는 `OverlayLayer`(원본 객체를 참조하지 않음). 셰이더/OpenGL 오류는 CI 가 못 잡으므로 PC 에서 확인 필요.
- Job: manifest 는 DataContractJsonSerializer(net48 호환, 외부 의존 없음). 리소스(ROI/모델/파라미터)는 실행 시 같은 이름의 변수로 로드된다. `JobRunner` 는 인스턴스당 한 스레드, 예외 대신 `JobResult.Code`.
- 2.5D(`Operators/Surface`): 스택 입력은 `ObjectArray<Image>` (스크립트에서 `[Img1, Img2, ...]` 로 전달 가능). `StackOps.Validate/ToFloat/ForRows` 로 검증·Float32 변환·행 병렬화. 좌표 규약: 위치는 픽셀 인덱스(픽셀 중심 = 정수), 기울기 GradientX = dz/dcol, GradientY = dz/drow, 높이 z 는 카메라 쪽이 +. 라인/디스패리티의 "없음" 은 -1.
- GPU 백엔드: 두 경로. (1) **OpenCL (OpenCV T-API/UMat)** — `OpenClRuntime` 이 OpenCL.dll 로 GPU 를 탐지하면 `BackendSettings.IsGpuAvailable` 이 true 가 되고, `GpuFilterOperator`/`GpuBinaryOperator` 를 상속한 연산자(InputArray/OutputArray 만 쓰는 `Apply`)가 `ImageOps.FilterGpu/BinaryGpu` 로 UMat 에서 실행된다. Intel UHD 등 내장 GPU 에서도 동작. (2) **CUDA** — `InspectionView.Native` + `GpuRuntime`(메모리/동기화만, 커널 없음, `IVN_WITH_CUDA`). 새 이미지 연산자는 가능하면 `GpuFilterOperator` 로 작성한다 (`Mat` 전용 API 인 `ConvertTo`, `Split` 등을 쓰면 CPU 전용 `ImageToImageOperator`).
- 카메라(`Core/Cameras`): `ICameraProvider`(이름, IsAvailable, Enumerate, Open) + `ICamera`. 구현은 `CameraBase` 상속 (`RegisterParameter` 로 파라미터 테이블, `GrabCore(timeout)` 가 새 Image 반환, 연속 취득은 기본 스레드 루프 또는 SDK 콜백에서 `PublishFrame`). 스크립트 핸들은 `CameraHandle`(IconicObject). 벤더 코드는 `#if IV_HAS_PYLON` 등으로 감싸고 csproj 가 SDK 경로(`PYLON_DEV_DIR`, `MVCAM_SDK_PATH`, Spinnaker 설치 폴더)를 찾아 정의한다. 벤더 구현은 **미검증** (SDK 있는 PC 에서 확인 필요). 프레임 변환은 `CameraFrames.FromPixelFormat/FromBayer/FromRgb8`.
- 노드 UI: 스크립트가 원본이고 그래프는 뷰다. `GraphDocument.FromScript` 는 `Var = Op(args)` 대입문만 받으며(그 외 문장이 있으면 null + 문제 목록), `ToScript` 는 위상 정렬 순서로 줄을 만들고 생략된 입력 뒤는 이름 인자로 쓴다. 노드 위치는 각 줄 끝 `// @node x,y` 주석. App 의 `NodeGraphViewModel` 은 문서를 소유하고 화면 컬렉션(NodeVm/ConnectorVm/ConnectionVm)을 동기화한다. Nodify 테마는 App.xaml 에서 병합.
- 예외 정책: 연산자는 `VisionException` 만 던진다. 런타임 API 는 `OperatorInvoker.Invoke` / `TryExecute` 로 에러 코드를 반환한다.

## 스크립트 문법 (확정)
- 대입 `Name = Expr` (`:=` 도 허용), 주석 `//`, 문자열 `'...'` 또는 `"..."`, 튜플 `[a, b]`, 인덱스 `T[i]`, 멤버 `Res.Area`
- 호출 `Op(Arg, Name = Arg)`: 위치 인자 다음에 이름 인자. 출력 1개면 값, 여러 개면 결과 객체
- `if / elseif / else / endif`, `for I = A to B by S ... endfor`, `while ... endwhile`, `break`, `continue`
- 연산자: `+ - * / %`, `== != < <= > >=`, `and or not` (`&& || !` 도 허용). `/` 는 항상 실수 나눗셈
- 값은 Tuple(숫자/문자열/불리언, 원소별 연산 + 브로드캐스트), 아이코닉 객체, 결과 객체
- 내장 함수(PascalCase): Len, Abs, Sqrt, Round, Floor, Ceil, Int, Real, Str, Min, Max, Sum, Mean, Print, TypeOf, Display(Value, Window=0), ClearWindow(Window=0)
- 실행 모델: AST → 평탄화된 명령 리스트(`Compilation/Instruction`) → PC 기반 인터프리터. 브레이크포인트/한 줄 실행/PC 이동은 `ScriptEngine` 이 담당

## 확정 요구사항 (요약)
1. GPU 연산 (CPU/GPU 선택), 백엔드: 전역 설정 + 연산자별 오버라이드 (Auto 포함)
2. HDevelop 과 거의 동일한 개발 환경: F5/F6, 브레이크포인트, 실행 줄 하이라이트, 변수창(썸네일), 연산자별 실행 시간 + 실제 백엔드 표시
3. 카메라 연결(공통 인터페이스 + 벤더 플러그인: Spinnaker, pylon, Euresys/Matrox/Dalsa, Hikrobot MVS) + 이미지 로드
4. C# + 자체 스크립트(AST 기반, 대입/수식/if/for/while), 이후 같은 레지스트리/AST 위에 노드 UI(Nodify)
5. 데이터 타입: Image(8/16bit/Color/Float), Region(RLE + 마스크 캐시), XLD, Tuple, 객체 배열. 대용량은 엔진 자동 타일 처리, Host/Device 자동 동기화
6. 2.5D: Photometric Stereo, Depth from Focus, 레이저/구조광, 조명 합성. FocusMeasure 는 DFF 와 AutoFocus 공유
7. 뷰어: OpenTK GLWpfControl, 피라미드 + 타일 텍스처, 벡터 오버레이, 다중 창 도킹, 프로파일/히스토그램/ROI/3D 높이맵
8. 에디터: AvalonEdit, 실시간 파싱, 하이라이트/오류, 자동완성/파라미터 힌트 (레지스트리 메타데이터)
9. Job: 단일 zip 패키지(스크립트, 파라미터, ROI, 캘리브레이션, 모델, 버전), 입출력 터미널(ToolBlock 방식), C# Runtime + MFC 용 C++/CLI 브리지
10. 1차 연산자: ReadImage, WriteImage, Threshold, Connection, SelectShape, ReduceDomain, CropPart

## 로드맵
1. ~~1차 연산자 6개 (Cpu + Parallel) 와 테스트~~ (완료)
2. ~~Script 렉서/파서/인터프리터, 브레이크포인트, 백그라운드 실행/중지~~ (완료)
3. ~~App: 도킹 레이아웃, 에디터, 변수창(썸네일), 연산자 트리, OpenGL 뷰어(타일/밉맵, 오버레이, ROI, 프로파일), 히스토그램/라인 프로파일, 다중 그래픽 창~~ (완료) → 2.5D 3D 보기는 이후
4. ~~Runtime: Job zip, 터미널 바인딩, C# API, C++/CLI 브리지, IDE Job 메뉴/터미널 창~~ (완료)
5. 연산자 확장 ~~1차 필터/산술/모폴로지/영역~~, ~~2차 에지/XLD(EdgesSubPix, 피팅, 변환), MeasurePos, NCC 매칭, ListFiles~~, ~~3차 MeasurePairs, 회전/스케일 NCC 매칭(CreateNccModel 의 AngleStart/Extent/Step, ScaleMin/Max/Step → FindNccModel 의 Angle/Scale 출력)~~ (완료). 미구현: 에지 기반 shape 매칭, 카메라 캘리브레이션
6. ~~2.5D 연산자군 (`Operators/Surface`): PhotometricStereo(기울기/알베도/Frankot-Chellappa 높이), FocusMeasure + DepthFromFocus(FocusOps 공유, AutoFocus 도 사용), ExtractLaserLine / MeasureProfileSheetOfLight / DisparityToHeight, PhaseShiftDecode(위상 천이 디코딩), ComposeIllumination(조명 합성)~~ (완료). 미구현: 위상 언래핑, 시트오브라이트 기하 캘리브레이션, 3D 높이맵 보기
7. ~~카메라: 공통 인터페이스(`Core/Cameras`), File/Simulation 카메라, 프레임그래버 연산자(InfoFramegrabber, OpenFramegrabber, GrabImage, GrabImageStart/Stop/Async, Set/GetFramegrabberParam, CloseFramegrabber), IDE Camera 창(스냅/라이브/파라미터), 벤더 플러그인 Pylon/Spinnaker/Hikrobot(SDK 조건부 컴파일, 미검증) + Euresys/Matrox/Dalsa 스텁~~ (완료)
8. ~~GPU: OpenCL(UMat) 백엔드 — 필터/산술/그레이 모폴로지/Rgb1ToGray 가 Gpu 백엔드 제공, 실제 백엔드가 타이밍 창에 표시~~ (완료). 미구현: CUDA 커널, Threshold/Connection 등 영역 연산자의 GPU 경로
9. ~~노드 UI: `Script/Graph/GraphDocument`(스크립트 ↔ 그래프, `// @node x,y` 로 배치 저장) + App `Nodes/`(Nodify 편집기, Nodes 탭: From Script / To Script / Run / Auto Layout, 연산자 트리 더블클릭으로 노드 추가, 리터럴 편집, 타입/순환 검사)~~ (완료). 제한: 직선형 스크립트(연산자 호출 대입문)만 노드로 표현, if/for 가 있으면 스크립트 모드만
10. ~~AutoFocus(카메라 파라미터 스캔 + FocusOps, Simulation 카메라의 FocusPosition/TrueFocus 로 테스트), UnwrapPhase(품질 유도 언래핑)~~ (완료)
11. 남은 것: 시트오브라이트 기하 캘리브레이션, CUDA 커널, 영역 연산자 GPU, 3D 높이맵 보기, 에지 기반 shape 매칭, 카메라 캘리브레이션
