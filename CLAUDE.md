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
| src/InspectionView.Runtime | net8.0; net48 | Job 패키지(zip) 입출력, 입출력 터미널, 검사 프로그램 연동 |
| src/InspectionView.App | net8.0-windows WPF | IDE (AvalonDock, AvalonEdit, 이후 OpenTK GLWpfControl, Nodify) |
| tests/InspectionView.Core.Tests | net8.0 xunit | Core 단위 테스트 |

## 코딩 규칙
- C# 12, nullable enable, ImplicitUsings 비활성 (net48 호환을 위해 using 명시).
- Core/Script/Runtime 은 **net48 에서도 컴파일**되어야 한다: `required` 멤버, `System.Text.Json` 의존, net5+ 전용 API 사용 금지. `init` 은 폴리필 있음.
- 연산자 이름/파라미터/스크립트 식별자는 **PascalCase**. 파라미터 순서는 입력 먼저, 반환값은 출력. 출력이 여럿이면 결과 객체(`ResultObject`) 로 반환.
- 아이코닉 객체는 `IconicObject` 상속 (참조 카운트). 생성자는 소유권을 가지며, 전달/보관 시 `AddRef()`, 끝나면 `Release()`/`Dispose()`. 새로 만든 객체를 `ObjectArray` 에 넣을 때는 `AddOwned`.
- 이미지 픽셀 접근은 `Image.GetRow<T>` / `MatInterop.AsMat` (복사 없음). `Mat` 을 Image 보다 오래 살려두지 않는다.
- Region 은 불변. 마스크는 `ToMask()` 캐시를 쓰고 반환 Mat 을 수정/Dispose 하지 않는다.
- 새 연산자: `[Operator("Name", Category=...)]` 클래스 + `[Input]`/`[Output]` 프로퍼티 + `[Backend(BackendKind.Cpu|Parallel|Gpu)] void Run(OperatorContext ctx)`. 입력 검증은 `Validate()` 에서 `VisionException(ErrorCode)` 로. 최소 Cpu 백엔드는 항상 제공.
- OpenCV 우선. 병목이면 `Parallel.For`(ctx.CreateParallelOptions()) 또는 직접 구현 허용.
- 예외 정책: 연산자는 `VisionException` 만 던진다. 런타임 API 는 `OperatorInvoker.Invoke` / `TryExecute` 로 에러 코드를 반환한다.

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
2. Script 렉서/파서/인터프리터, 브레이크포인트, 백그라운드 실행/중지
3. App: 도킹 레이아웃, 에디터, 변수창, 연산자 트리, OpenGL 뷰어
4. Runtime: Job zip, 터미널 바인딩, C++/CLI 브리지
5. 카메라 플러그인, GPU 연산자, 2.5D
