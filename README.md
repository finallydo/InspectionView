# InspectionView

[![build](https://github.com/finallydo/InspectionView/actions/workflows/build.yml/badge.svg)](https://github.com/finallydo/InspectionView/actions/workflows/build.yml)

OpenCV 기반 Halcon(HDevelop) 대체 머신비전 개발 툴.
스크립트(자체 인터프리터) + 연산자 레지스트리 + CPU/Parallel/GPU 백엔드 위에
WPF IDE, 노드 UI, Job 런타임(검사 프로그램 연동)을 올리는 구조이다.

## 솔루션 구조

```
InspectionView/
├─ InspectionView.sln
├─ Directory.Build.props              공통 빌드 설정 (x64 고정, C# 12, nullable)
├─ src/
│  ├─ InspectionView.Core/            [net8.0; net48]  데이터 타입 / 레지스트리 / 백엔드 / 기본 연산자
│  │  ├─ Memory/     IconicObject(참조 카운트), NativeBuffer(정렬 Host 메모리),
│  │  │              IDeviceAllocator, HostDeviceStorage(Host/Device 자동 동기화)
│  │  ├─ Types/      PixelType, Geometry, Image, Run/Region(RLE + 마스크 캐시),
│  │  │              XldContour, TupleValue, ObjectArray<T>
│  │  ├─ Tiling/     TileScheme (대형/라인스캔 타일 분할)
│  │  ├─ Backends/   BackendKind, BackendSettings(전역 + 연산자별 오버라이드), GpuRuntime(P/Invoke)
│  │  ├─ Registry/   Operator/Input/Output/Backend Attribute, OperatorDescriptor,
│  │  │              OperatorBase, OperatorContext(프로파일러), OperatorRegistry(플러그인 로드), OperatorInvoker
│  │  ├─ Interop/    MatInterop (Image <-> OpenCvSharp Mat, 복사 없는 래핑)
│  │  └─ Operators/  IO(ReadImage, WriteImage), Segmentation(Threshold),
│  │                 Regions(Connection, SelectShape), Images(ReduceDomain, CropPart)
│  ├─ InspectionView.Native/          [C++ DLL]  GPU 백엔드 (CUDA 옵션, 없으면 device_count = 0)
│  ├─ InspectionView.Script/          [net8.0; net48]  스크립트 AST (파서/인터프리터는 다음 단계)
│  ├─ InspectionView.Runtime/         [net8.0; net48]  Job 패키지 manifest (zip 입출력/실행기는 다음 단계)
│  └─ InspectionView.App/             [net8.0-windows WPF]  IDE 껍데기 (AvalonDock / AvalonEdit 참조)
├─ tests/
│  └─ InspectionView.Core.Tests/      xunit (Region 집합연산, Image/Mat 상호운용, 레지스트리, ReadImage)
└─ plugins/                           연산자 플러그인 DLL 폴더 (실행 시 Plugins/ 로 복사)
```

## 빌드

- Visual Studio 2022 (17.8+), .NET 8 SDK, "C++ 데스크톱 개발" 워크로드 (Native 프로젝트용)
- `InspectionView.sln` 을 열고 **x64** 로 빌드한다.
- CLI: `msbuild InspectionView.sln -restore -p:Configuration=Debug -p:Platform=x64` (C++ Native 프로젝트가 있어 `dotnet build` 솔루션 빌드는 불가, C# 프로젝트 단위는 가능)
- 테스트: `dotnet test tests/InspectionView.Core.Tests -p:Platform=x64`
- CI: push 마다 GitHub Actions (windows-latest) 에서 솔루션 빌드 + Core 테스트 실행
- GPU: `InspectionView.Native` 에 CUDA 빌드 사용자 지정을 추가하고 `IVN_WITH_CUDA` 를 정의, `cudart.lib` 링크.
  빌드된 `InspectionView.Native.dll` 을 실행 폴더에 두면 `GpuRuntime.IsAvailable` 이 true 가 된다.

## 핵심 설계

### 메모리 / 수명
- 모든 아이코닉 객체(`Image`, `Region`, `XldContour`, `ObjectArray<T>`)는 `IconicObject` 를 상속해 참조 카운트로 관리한다.
  생성 시 1, `AddRef()` / `Release()`(= `Dispose()`), 0 이 되면 Host/Device 메모리를 즉시 해제한다.
- `Image` 픽셀은 `HostDeviceStorage` 가 소유한다. `AcquireHost(forWrite)` / `AcquireDevice(allocator, forWrite)` 가
  어느 쪽이 최신인지 추적해 필요한 순간에만 업로드/다운로드한다. `ReduceDomain` 은 저장소를 공유하므로 복사가 없다.
- `NativeBuffer` 는 64 byte 정렬된 비관리 메모리로 수 GB 라인스캔 이미지도 담는다. OpenCV `Mat` 은 `MatInterop.AsMat` 으로
  복사 없이 감싼다 (레이아웃은 OpenCV 와 같은 인터리브, 컬러는 BGR 순서 유지).

### Region
- RLE(`Run[]`, ColEnd 포함) 기반 불변 객체. 생성 시 정렬/병합되며 `Union / Intersection / Difference / Clip / Translate` 제공.
- OpenCV/GPU 연산용 마스크는 `ToMask(bounds)` 로 얻고, 같은 bounds 요청은 캐시를 돌려준다 (반환 Mat 은 수정/Dispose 금지).

### 연산자 정의 (레지스트리)
```csharp
[Operator("Threshold", Category = "Segmentation", Description = "...")]
public sealed class Threshold : OperatorBase
{
    [Input] public Image? Image { get; set; }
    [Input(Default = 128.0, Min = 0)] public double MinGray { get; set; }
    [Input(Default = 255.0)]          public double MaxGray { get; set; }
    [Output] public Region? Region { get; private set; }

    [Backend(BackendKind.Cpu)]      void RunCpu(OperatorContext ctx) { ... }
    [Backend(BackendKind.Parallel)] void RunParallel(OperatorContext ctx) { ... }
    [Backend(BackendKind.Gpu)]      void RunGpu(OperatorContext ctx) { ... }
}
```
- `[Input]` 선언 순서 = 스크립트 위치 인자 순서. 출력이 하나면 그 값이, 여럿이면 `ResultObject` 가 반환되어 `Res.Area` 로 접근한다.
- 어셈블리 로드 시 Attribute 리플렉션으로 자동 등록되고, `OperatorRegistry.LoadPlugins("Plugins")` 로 플러그인 DLL 을 추가한다.
- 백엔드 선택: `BackendSettings.Global.DefaultBackend` + `SetOverride("Threshold", BackendKind.Gpu)`.
  요청 백엔드가 없거나 GPU 가 없으면 Gpu → Parallel → Cpu 순으로 폴백하며, `ExecutionRecord` 에 요청/실제 백엔드와 실행 시간이 남는다.
- 오류: 연산자는 `VisionException(ErrorCode)` 를 던진다. IDE 는 예외로 해당 줄에서 정지하고,
  런타임은 `OperatorInvoker.Invoke(...)` / `OperatorBase.TryExecute(...)` 로 에러 코드를 받는다.

### 스크립트에서의 호출 흐름
```
Image = ReadImage('C:/img/a.png')
   → OperatorInvoker.Invoke(registry, "ReadImage", [ 'C:/img/a.png' ], ctx)
   → OperatorDescriptor.SelectBackend → ReadImage.RunCpu → OperatorResult.ReturnValue (Image)
```

## 다음 단계
1. Script: 렉서/파서 → AST, 인터프리터(변수 테이블, 브레이크포인트, 줄 단위 실행), 백그라운드 실행/중지
2. App: AvalonDock 레이아웃, AvalonEdit 하이라이트/자동완성(레지스트리 메타데이터), 변수창, 연산자 트리, OpenGL 뷰어(피라미드/타일 텍스처)
3. Runtime: Job zip 입출력, 입출력 터미널 바인딩, C++/CLI 브리지
4. 카메라 공통 인터페이스 + 벤더 플러그인, 2.5D 연산자군
