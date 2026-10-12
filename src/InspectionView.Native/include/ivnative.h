// InspectionView.Native - GPU 백엔드 C API
// C# 측 선언: InspectionView.Core/Backends/GpuRuntime.cs (Native 클래스) 와 반드시 일치시킬 것.
#pragma once
#include <stdint.h>

#ifdef _WIN32
#  ifdef IVN_EXPORTS
#    define IVN_API extern "C" __declspec(dllexport)
#  else
#    define IVN_API extern "C" __declspec(dllimport)
#  endif
#else
#  define IVN_API extern "C" __attribute__((visibility("default")))
#endif

#define IVN_VERSION_MAJOR 0
#define IVN_VERSION_MINOR 1
#define IVN_VERSION_PATCH 0

// 상태 코드 (0 = 성공)
enum ivn_status
{
    IVN_OK = 0,
    IVN_ERR_NO_DEVICE = 1,
    IVN_ERR_INVALID_ARG = 2,
    IVN_ERR_OUT_OF_MEMORY = 3,
    IVN_ERR_CUDA = 4,
};

IVN_API int ivn_get_version();                 // major*10000 + minor*100 + patch
IVN_API int ivn_device_count();                // 사용 가능한 CUDA 디바이스 수 (CUDA 없이 빌드하면 0)
IVN_API int ivn_set_device(int device);
IVN_API int ivn_malloc(int64_t bytes, void** devicePtr);
IVN_API int ivn_free(void* devicePtr);
IVN_API int ivn_memcpy_h2d(void* devicePtr, const void* hostPtr, int64_t bytes);
IVN_API int ivn_memcpy_d2h(void* hostPtr, const void* devicePtr, int64_t bytes);
IVN_API int ivn_synchronize();
