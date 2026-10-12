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
- 새 연산자: `[Operator("Name", Category=...)]` 클래스 + `[Input]`/`[Output]` 프로퍼티 + `[Backend(BackendKind.Cpu|Parallel|Gpu)] void Run(OperatorContext ctx)`. 입력 검증은 `Validate()` 에서 `VisionException(ErrorCode)` 로. 최소 Cpu 백엔드는 항상 제공.
- OpenCV 우선. 병목이면 `Parallel.For`(ctx.CreateParallelOptions()) 또는 직접 구현 허용.
- WPF: 엔진 이벤트는 실행 스레드에서 오므로 뷰모델은 변경을 모아 두고 100ms DispatcherTimer(`MainViewModel.Tick`)에서 반영한다. UI 코드는 아이코닉 객체를 AddRef/Release 하지 않는다 (VariableTable 소유). 예외: `Viewer/ImageViewer` 는 표시 중인 Image 를 AddRef 해 소유한다.
- 뷰어(`App/Viewer`): OpenTK 4 + GLWpfControl, GL 3.3 core. 모든 GL 호출은 Render 콜백 안에서만 (업로드/삭제는 pending 큐로 미룸). 이미지는 `ImageTextureSet`(≤4096 타일 + 밉맵), 오버레이는 `OverlayLayer`(원본 객체를 참조하지 않음). 셰이더/OpenGL 오류는 CI 가 못 잡으므로 PC 에서 확인 필요.
- Job: manifest 는 DataContractJsonSerializer(net48 호환, 외부 의존 없음). 리소스(ROI/모델/파라미터)는 실행 시 같은 이름의 변수로 로드된다. `JobRunner` 는 인스턴스당 한 스레드, 예외 대신 `JobResult.Code`.
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
5. 카메라 플러그인, GPU 연산자, 2.5D
