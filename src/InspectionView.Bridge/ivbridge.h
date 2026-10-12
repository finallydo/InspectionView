// InspectionView.Bridge - MFC/네이티브 C++ 검사 프로그램용 C API (C++/CLI 로 구현, .NET Framework 4.8)
//
// 사용 예:
//   void* job = ivb_job_load(L"C:\\jobs\\front.ivjob");
//   if (!job) { ... ivb_last_error() ... }
//   ivb_job_set_image(job, "Image", pBits, w, h, stride, IVB_PIXEL_UINT8, 1);
//   int line = 0;
//   int code = ivb_job_run(job, &line);            // 0 = OK, 그 외 ErrorCode (Core/Errors/ErrorCode.cs)
//   double count; ivb_job_get_tuple_double(job, "Count", &count, 1);
//   ivb_job_free(job);
//
// 모든 함수는 예외를 던지지 않는다. 실패하면 0 이 아닌 코드를 돌려주고 ivb_last_error() 에 메시지를 남긴다.
#pragma once
#include <stdint.h>

#ifdef IVB_EXPORTS
#  define IVB_API extern "C" __declspec(dllexport)
#else
#  define IVB_API extern "C" __declspec(dllimport)
#endif

// InspectionView.Core.Types.PixelType 과 같은 순서
enum ivb_pixel_type
{
    IVB_PIXEL_UINT8 = 0,
    IVB_PIXEL_INT16 = 1,
    IVB_PIXEL_UINT16 = 2,
    IVB_PIXEL_INT32 = 3,
    IVB_PIXEL_FLOAT32 = 4,
    IVB_PIXEL_FLOAT64 = 5,
};

// 자주 쓰는 에러 코드 (전체 목록은 Core/Errors/ErrorCode.cs)
enum ivb_error
{
    IVB_OK = 0,
    IVB_ERR_UNKNOWN = 1,
    IVB_ERR_INVALID_ARGUMENT = 1000,
    IVB_ERR_TYPE_MISMATCH = 1003,
    IVB_ERR_MISSING_ARGUMENT = 1004,
    IVB_ERR_FILE_NOT_FOUND = 2000,
    IVB_ERR_OPERATOR_NOT_FOUND = 3000,
    IVB_ERR_CANCELLED = 6000,
};

typedef void* ivb_job;

// ---- 수명 ----
IVB_API ivb_job ivb_job_load(const wchar_t* path);          // 실패 시 NULL
IVB_API void    ivb_job_free(ivb_job job);
IVB_API const wchar_t* ivb_last_error();                     // 스레드별 마지막 오류 메시지
IVB_API int     ivb_version();                               // major*10000 + minor*100 + patch

// ---- 터미널 정보 ----
IVB_API int ivb_job_input_count(ivb_job job);
IVB_API int ivb_job_output_count(ivb_job job);
IVB_API int ivb_job_input_name(ivb_job job, int index, wchar_t* buffer, int bufferChars);   // 이름 길이 반환
IVB_API int ivb_job_output_name(ivb_job job, int index, wchar_t* buffer, int bufferChars);

// ---- 입력 ----
IVB_API int ivb_job_set_image(ivb_job job, const char* name, const void* data, int width, int height, int64_t stride, int pixelType, int channels);
IVB_API int ivb_job_set_double(ivb_job job, const char* name, double value);
IVB_API int ivb_job_set_doubles(ivb_job job, const char* name, const double* values, int count);
IVB_API int ivb_job_set_string(ivb_job job, const char* name, const wchar_t* value);
IVB_API int ivb_job_set_region_rect(ivb_job job, const char* name, int row1, int col1, int row2, int col2);

// ---- 실행 ----
IVB_API int ivb_job_run(ivb_job job, int* errorLine);        // 0 = OK
IVB_API double ivb_job_last_elapsed_ms(ivb_job job);

// ---- 출력 ----
IVB_API int ivb_job_get_tuple_length(ivb_job job, const char* name);                         // < 0 이면 오류
IVB_API int ivb_job_get_doubles(ivb_job job, const char* name, double* values, int maxCount); // 복사한 개수, < 0 이면 오류
IVB_API int ivb_job_get_string(ivb_job job, const char* name, int index, wchar_t* buffer, int bufferChars);
IVB_API int ivb_job_get_image_info(ivb_job job, const char* name, int* width, int* height, int* pixelType, int* channels);
IVB_API int ivb_job_get_image_data(ivb_job job, const char* name, void* destination, int64_t dstStride);
IVB_API int ivb_job_get_region_run_count(ivb_job job, const char* name);                      // < 0 이면 오류
IVB_API int ivb_job_get_region_runs(ivb_job job, const char* name, int32_t* runs, int maxRuns); // (row, colBegin, colEnd) 삼중, 복사한 run 수
IVB_API int ivb_job_get_region_area(ivb_job job, const char* name, int64_t* area);
