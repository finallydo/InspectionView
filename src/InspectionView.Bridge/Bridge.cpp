// InspectionView.Bridge - C API 구현 (C++/CLI)
#define IVB_EXPORTS
#include "ivbridge.h"

#include <vcclr.h>
#include <cwchar>
#include <string>

using namespace System;
using namespace System::Collections::Generic;
using namespace InspectionView::Core::Types;
using namespace InspectionView::Core::Errors;
using namespace InspectionView::Runtime;

namespace
{
    // 핸들: InspectionJob 을 gcroot 로 감싼 네이티브 객체
    struct JobHandle
    {
        gcroot<InspectionJob^> job;
        double lastElapsedMs = 0;
    };

    // 마지막 오류 메시지 (전역; 여러 스레드에서 동시에 쓰면 섞일 수 있음)
    std::wstring g_lastError;

    void SetError(String^ message)
    {
        pin_ptr<const wchar_t> p = PtrToStringChars(message);
        g_lastError = p;
    }

    int Fail(int code, String^ message)
    {
        SetError(message);
        return code;
    }

    int Fail(Exception^ ex)
    {
        VisionException^ ve = dynamic_cast<VisionException^>(ex);
        SetError(ex->Message);
        return ve != nullptr ? (int)ve->Code : IVB_ERR_UNKNOWN;
    }

    String^ ToManaged(const char* s) { return s ? gcnew String(s) : String::Empty; }
    String^ ToManaged(const wchar_t* s) { return s ? gcnew String(s) : String::Empty; }

    int CopyString(String^ text, wchar_t* buffer, int bufferChars)
    {
        if (buffer != nullptr && bufferChars > 0)
        {
            pin_ptr<const wchar_t> p = PtrToStringChars(text);
            int n = text->Length < bufferChars - 1 ? text->Length : bufferChars - 1;
            wmemcpy(buffer, p, n);
            buffer[n] = L'\0';
        }
        return text->Length;
    }

    JobHandle* Handle(ivb_job job) { return static_cast<JobHandle*>(job); }

    // 마지막 결과에서 출력 값 얻기 (없으면 nullptr + 에러 설정)
    Object^ Output(JobHandle* h, const char* name)
    {
        JobResult^ r = h->job->LastResult;
        if (r == nullptr) { SetError("Job has not been run."); return nullptr; }
        Object^ v = nullptr;
        if (!r->TryGetOutput(ToManaged(name), v) || v == nullptr)
        {
            SetError(String::Format("Output '{0}' is not available.", ToManaged(name)));
            return nullptr;
        }
        return v;
    }
}

IVB_API int ivb_version()
{
    Version^ v = InspectionJob::typeid->Assembly->GetName()->Version;
    return v->Major * 10000 + v->Minor * 100 + v->Build;
}

IVB_API const wchar_t* ivb_last_error()
{
    return g_lastError.c_str();
}

IVB_API ivb_job ivb_job_load(const wchar_t* path)
{
    try
    {
        JobHandle* h = new JobHandle();
        h->job = InspectionJob::Load(ToManaged(path));
        g_lastError.clear();
        return h;
    }
    catch (Exception^ ex)
    {
        Fail(ex);
        return nullptr;
    }
}

IVB_API void ivb_job_free(ivb_job job)
{
    JobHandle* h = Handle(job);
    if (h == nullptr) return;
    try { delete static_cast<InspectionJob^>(h->job); } catch (Exception^) {}
    delete h;
}

IVB_API int ivb_job_input_count(ivb_job job)
{
    JobHandle* h = Handle(job);
    return h ? h->job->Manifest->Inputs->Count : -1;
}

IVB_API int ivb_job_output_count(ivb_job job)
{
    JobHandle* h = Handle(job);
    return h ? h->job->Manifest->Outputs->Count : -1;
}

IVB_API int ivb_job_input_name(ivb_job job, int index, wchar_t* buffer, int bufferChars)
{
    JobHandle* h = Handle(job);
    if (!h || index < 0 || index >= h->job->Manifest->Inputs->Count) return -1;
    return CopyString(h->job->Manifest->Inputs[index]->Name, buffer, bufferChars);
}

IVB_API int ivb_job_output_name(ivb_job job, int index, wchar_t* buffer, int bufferChars)
{
    JobHandle* h = Handle(job);
    if (!h || index < 0 || index >= h->job->Manifest->Outputs->Count) return -1;
    return CopyString(h->job->Manifest->Outputs[index]->Name, buffer, bufferChars);
}

IVB_API int ivb_job_set_image(ivb_job job, const char* name, const void* data, int width, int height, int64_t stride, int pixelType, int channels)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    try
    {
        h->job->SetInputImage(ToManaged(name), IntPtr(const_cast<void*>(data)), width, height, stride, (PixelType)pixelType, channels);
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API int ivb_job_set_double(ivb_job job, const char* name, double value)
{
    return ivb_job_set_doubles(job, name, &value, 1);
}

IVB_API int ivb_job_set_doubles(ivb_job job, const char* name, const double* values, int count)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    try
    {
        array<double>^ arr = gcnew array<double>(count < 0 ? 0 : count);
        for (int i = 0; i < arr->Length; i++) arr[i] = values[i];
        h->job->SetInput(ToManaged(name), TupleValue::From(arr));
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API int ivb_job_set_string(ivb_job job, const char* name, const wchar_t* value)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    try
    {
        h->job->SetInput(ToManaged(name), ToManaged(value));
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API int ivb_job_set_region_rect(ivb_job job, const char* name, int row1, int col1, int row2, int col2)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    try
    {
        Region^ region = Region::FromRectangle1(row1, col1, row2, col2);
        try { h->job->SetInput(ToManaged(name), region); }
        finally { region->Release(); }
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API int ivb_job_run(ivb_job job, int* errorLine)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    if (errorLine) *errorLine = 0;
    try
    {
        JobResult^ result = h->job->Run();
        h->lastElapsedMs = result->Elapsed.TotalMilliseconds;
        if (!result->Success)
        {
            if (errorLine) *errorLine = result->Line;
            return Fail((int)result->Code, result->Message);
        }
        g_lastError.clear();
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API double ivb_job_last_elapsed_ms(ivb_job job)
{
    JobHandle* h = Handle(job);
    return h ? h->lastElapsedMs : -1;
}

IVB_API int ivb_job_get_tuple_length(ivb_job job, const char* name)
{
    JobHandle* h = Handle(job);
    if (!h) return -1;
    Object^ v = Output(h, name);
    if (v == nullptr) return -1;
    try { return TupleValue::From(v)->Length; }
    catch (Exception^ ex) { Fail(ex); return -1; }
}

IVB_API int ivb_job_get_doubles(ivb_job job, const char* name, double* values, int maxCount)
{
    JobHandle* h = Handle(job);
    if (!h) return -1;
    Object^ v = Output(h, name);
    if (v == nullptr) return -1;
    try
    {
        TupleValue^ t = TupleValue::From(v);
        int n = t->Length < maxCount ? t->Length : maxCount;
        for (int i = 0; i < n; i++) values[i] = t->D(i);
        return n;
    }
    catch (Exception^ ex) { Fail(ex); return -1; }
}

IVB_API int ivb_job_get_string(ivb_job job, const char* name, int index, wchar_t* buffer, int bufferChars)
{
    JobHandle* h = Handle(job);
    if (!h) return -1;
    Object^ v = Output(h, name);
    if (v == nullptr) return -1;
    try
    {
        TupleValue^ t = TupleValue::From(v);
        if (index < 0 || index >= t->Length) { SetError("Index out of range."); return -1; }
        return CopyString(t->S(index), buffer, bufferChars);
    }
    catch (Exception^ ex) { Fail(ex); return -1; }
}

IVB_API int ivb_job_get_image_info(ivb_job job, const char* name, int* width, int* height, int* pixelType, int* channels)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    Object^ v = Output(h, name);
    if (v == nullptr) return IVB_ERR_INVALID_ARGUMENT;
    Image^ img = dynamic_cast<Image^>(v);
    if (img == nullptr) return Fail(IVB_ERR_TYPE_MISMATCH, "Output is not an image.");
    if (width) *width = img->Width;
    if (height) *height = img->Height;
    if (pixelType) *pixelType = (int)img->PixelType;
    if (channels) *channels = img->Channels;
    return IVB_OK;
}

IVB_API int ivb_job_get_image_data(ivb_job job, const char* name, void* destination, int64_t dstStride)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    Object^ v = Output(h, name);
    if (v == nullptr) return IVB_ERR_INVALID_ARGUMENT;
    Image^ img = dynamic_cast<Image^>(v);
    if (img == nullptr) return Fail(IVB_ERR_TYPE_MISMATCH, "Output is not an image.");
    try
    {
        img->CopyTo(IntPtr(destination), dstStride);
        return IVB_OK;
    }
    catch (Exception^ ex) { return Fail(ex); }
}

IVB_API int ivb_job_get_region_run_count(ivb_job job, const char* name)
{
    JobHandle* h = Handle(job);
    if (!h) return -1;
    Object^ v = Output(h, name);
    Region^ r = dynamic_cast<Region^>(v);
    if (r == nullptr) { SetError("Output is not a region."); return -1; }
    return r->RunCount;
}

IVB_API int ivb_job_get_region_runs(ivb_job job, const char* name, int32_t* runs, int maxRuns)
{
    JobHandle* h = Handle(job);
    if (!h) return -1;
    Object^ v = Output(h, name);
    Region^ r = dynamic_cast<Region^>(v);
    if (r == nullptr) { SetError("Output is not a region."); return -1; }
    int n = r->RunCount < maxRuns ? r->RunCount : maxRuns;
    for (int i = 0; i < n; i++)
    {
        Run run = r->Runs[i];
        runs[3 * i] = run.Row;
        runs[3 * i + 1] = run.ColBegin;
        runs[3 * i + 2] = run.ColEnd;
    }
    return n;
}

IVB_API int ivb_job_get_region_area(ivb_job job, const char* name, int64_t* area)
{
    JobHandle* h = Handle(job);
    if (!h) return Fail(IVB_ERR_INVALID_ARGUMENT, "Invalid job handle.");
    Object^ v = Output(h, name);
    Region^ r = dynamic_cast<Region^>(v);
    if (r == nullptr) return Fail(IVB_ERR_TYPE_MISMATCH, "Output is not a region.");
    if (area) *area = r->Area;
    return IVB_OK;
}
