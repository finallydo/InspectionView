// InspectionView.Native - GPU 백엔드 구현.
// IVN_WITH_CUDA 를 정의하고 CUDA 툴킷을 링크하면 실제 디바이스를 사용하고,
// 그렇지 않으면 device_count 가 0 을 돌려주어 Core 가 GPU 백엔드를 건너뛴다.
// (메모리 함수는 CUDA 없이도 host malloc 으로 동작하므로 동기화 로직 테스트에 쓸 수 있다)
#define IVN_EXPORTS
#include "../include/ivnative.h"

#include <cstdlib>
#include <cstring>

#ifdef IVN_WITH_CUDA
#include <cuda_runtime.h>
static int to_status(cudaError_t e) { return e == cudaSuccess ? IVN_OK : IVN_ERR_CUDA; }
#endif

IVN_API int ivn_get_version()
{
    return IVN_VERSION_MAJOR * 10000 + IVN_VERSION_MINOR * 100 + IVN_VERSION_PATCH;
}

IVN_API int ivn_device_count()
{
#ifdef IVN_WITH_CUDA
    int n = 0;
    if (cudaGetDeviceCount(&n) != cudaSuccess) return 0;
    return n;
#else
    return 0;
#endif
}

IVN_API int ivn_set_device(int device)
{
#ifdef IVN_WITH_CUDA
    return to_status(cudaSetDevice(device));
#else
    (void)device;
    return IVN_OK;
#endif
}

IVN_API int ivn_malloc(int64_t bytes, void** devicePtr)
{
    if (devicePtr == nullptr || bytes < 0) return IVN_ERR_INVALID_ARG;
#ifdef IVN_WITH_CUDA
    return to_status(cudaMalloc(devicePtr, (size_t)bytes));
#else
    *devicePtr = std::malloc(bytes > 0 ? (size_t)bytes : 1);
    return *devicePtr ? IVN_OK : IVN_ERR_OUT_OF_MEMORY;
#endif
}

IVN_API int ivn_free(void* devicePtr)
{
    if (devicePtr == nullptr) return IVN_OK;
#ifdef IVN_WITH_CUDA
    return to_status(cudaFree(devicePtr));
#else
    std::free(devicePtr);
    return IVN_OK;
#endif
}

IVN_API int ivn_memcpy_h2d(void* devicePtr, const void* hostPtr, int64_t bytes)
{
    if (devicePtr == nullptr || hostPtr == nullptr || bytes < 0) return IVN_ERR_INVALID_ARG;
#ifdef IVN_WITH_CUDA
    return to_status(cudaMemcpy(devicePtr, hostPtr, (size_t)bytes, cudaMemcpyHostToDevice));
#else
    std::memcpy(devicePtr, hostPtr, (size_t)bytes);
    return IVN_OK;
#endif
}

IVN_API int ivn_memcpy_d2h(void* hostPtr, const void* devicePtr, int64_t bytes)
{
    if (devicePtr == nullptr || hostPtr == nullptr || bytes < 0) return IVN_ERR_INVALID_ARG;
#ifdef IVN_WITH_CUDA
    return to_status(cudaMemcpy(hostPtr, devicePtr, (size_t)bytes, cudaMemcpyDeviceToHost));
#else
    std::memcpy(hostPtr, devicePtr, (size_t)bytes);
    return IVN_OK;
#endif
}

IVN_API int ivn_synchronize()
{
#ifdef IVN_WITH_CUDA
    return to_status(cudaDeviceSynchronize());
#else
    return IVN_OK;
#endif
}
