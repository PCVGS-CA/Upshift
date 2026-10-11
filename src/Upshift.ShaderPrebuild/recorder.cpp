// upshift_rec.asi: Upshift's pipeline recorder ("Pre-build shaders", experimental). OptiScaler loads it as a plugin
// when Upshift has set the game's OptiScaler.ini to ([Plugins] Path = Upshift's plugins folder, LoadAsiPlugins = true).
//
// It passes every pipeline creation through unchanged and, after each successful one, keeps what's needed to make the
// same call again (recording.h) plus a timing line. Nothing is written to the game folder: the files go to
// %LOCALAPPDATA%\Upshift\shaderwarm\<game program>\. Upshift merges each finished recording into one library per game
// and graphics card model.
//
// The game's own threads only hash and copy: shader files, pipeline records and timing lines go into a queue that a
// writer thread of the recorder's own saves to disk, so a game creating thousands of pipelines at start-up never
// waits for the disk.
//
// How it sees the calls: Direct3D 12 devices share one table of functions (their vtable) in the Direct3D 12 runtime.
// The recorder creates a small device of its own (feature level 1_0_CORE, which OptiScaler leaves alone), and swaps
// these entries in that shared table for its own functions, which call the originals:
//   ID3D12Device   10 CreateGraphicsPipelineState, 11 CreateComputePipelineState, 16 CreateRootSignature
//   ID3D12Device1  44 CreatePipelineLibrary (then ID3D12PipelineLibrary 9 LoadGraphicsPipeline, 10 LoadComputePipeline,
//                  and ID3D12PipelineLibrary1 13 LoadPipeline)
//   ID3D12Device2  47 CreatePipelineState (pipeline state streams)
//   ID3D12Device5  62 CreateStateObject, 63 GetRaytracingAccelerationStructurePrebuildInfo: only noted, not recorded
//                  (they show whether the game builds ray tracing pipelines or scenes)
// The indices are the method order in Microsoft's d3d12.h. Its own device is kept alive so the runtime stays loaded.
#include "adapter.h"
#include "recording.h"

#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <unordered_set>

using Microsoft::WRL::ComPtr;
using namespace warm;

namespace
{
using PFN_Gfx = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_Cmp = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, const D3D12_COMPUTE_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_Rs = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device*, UINT, const void*, SIZE_T, REFIID, void**);
using PFN_Lib = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device1*, const void*, SIZE_T, REFIID, void**);
using PFN_Stream = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device2*, const D3D12_PIPELINE_STATE_STREAM_DESC*, REFIID, void**);
using PFN_LibGfx = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary*, LPCWSTR, const D3D12_GRAPHICS_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_LibCmp = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary*, LPCWSTR, const D3D12_COMPUTE_PIPELINE_STATE_DESC*, REFIID, void**);
using PFN_LibStream = HRESULT(STDMETHODCALLTYPE*)(ID3D12PipelineLibrary1*, LPCWSTR, const D3D12_PIPELINE_STATE_STREAM_DESC*, REFIID, void**);
using PFN_StateObject = HRESULT(STDMETHODCALLTYPE*)(ID3D12Device5*, const D3D12_STATE_OBJECT_DESC*, REFIID, void**);
using PFN_Prebuild = void(STDMETHODCALLTYPE*)(ID3D12Device5*, const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS*,
                                             D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO*);

PFN_Gfx o_gfx;
PFN_Cmp o_cmp;
PFN_Rs o_rs;
PFN_Lib o_lib;
PFN_Stream o_stream;
PFN_StateObject o_stateObject;
PFN_Prebuild o_prebuild;
PFN_LibGfx o_libGfx;
PFN_LibCmp o_libCmp;
PFN_LibStream o_libStream;
std::atomic<bool> g_sceneNoted{ false };

std::mutex g_lock;   // the sets below, and what's queued for writing
std::unordered_map<ID3D12RootSignature*, uint64_t> g_rootSignatures;
std::unordered_set<uint64_t> g_blobs, g_records, g_refusedIds, g_luids;
fs::path g_blobDir, g_session;
double g_start = 0;
std::atomic<bool> g_recording{ false };
ComPtr<ID3D12Device> g_ownDevice;   // keeps the Direct3D 12 runtime (and so the swapped table) loaded
std::once_flag g_libraryPatched;

// ---------------------------------------------------------------- the writer thread

/// One thing to save: a shader file, a pipeline record (or a refused one), a timing line or a session note.
struct Job
{
    enum class Kind { Blob, Record, Refused, Timing, Note } kind;
    uint64_t hash = 0;             // Blob: its name; Record/Refused: the record id
    RecordKind recordKind = RecordKind::Graphics;
    std::vector<uint8_t> bytes;    // Blob, Record, Refused
    std::string line;              // Timing, Note
};

std::mutex g_queueLock;
std::condition_variable g_queueReady;
std::vector<Job> g_queue;
std::atomic<bool> g_saving{ false };   // the writer thread is in Save()
FILE* g_pipelines = nullptr;
FILE* g_refused = nullptr;
FILE* g_timing = nullptr;

void Enqueue(Job job)
{
    {
        std::lock_guard g(g_queueLock);
        g_queue.push_back(std::move(job));
    }
    g_queueReady.notify_one();
}

void WriteRecord(FILE* f, RecordKind kind, uint64_t id, const std::vector<uint8_t>& bytes)
{
    if (!f) return;
    uint32_t k = static_cast<uint32_t>(kind), size = static_cast<uint32_t>(bytes.size());
    fwrite(&k, 4, 1, f);
    fwrite(&id, 8, 1, f);
    fwrite(&size, 4, 1, f);
    fwrite(bytes.data(), 1, bytes.size(), f);
}

/// Saves a batch. Shader files are written before the records that use them, so a record on disk never points at a
/// shader that isn't there yet.
void Save(std::vector<Job>& batch)
{
    std::string notes;
    for (auto& j : batch)
        if (j.kind == Job::Kind::Blob)
        {
            fs::path file = g_blobDir / Widen(Hex(j.hash) + ".bin");
            std::error_code ec;
            if (!fs::exists(file, ec))
            {
                // Written under a temporary name, then renamed: a half-written file never has the real name.
                fs::path temp = file;
                temp += L".tmp";
                if (FILE* f = _wfopen(temp.c_str(), L"wb"))
                {
                    fwrite(j.bytes.data(), 1, j.bytes.size(), f);
                    fclose(f);
                    fs::rename(temp, file, ec);
                }
            }
        }
    for (auto& j : batch)
        switch (j.kind)
        {
        case Job::Kind::Record: WriteRecord(g_pipelines, j.recordKind, j.hash, j.bytes); break;
        case Job::Kind::Refused: WriteRecord(g_refused, j.recordKind, j.hash, j.bytes); break;
        case Job::Kind::Timing: if (g_timing) fputs(j.line.c_str(), g_timing); break;
        case Job::Kind::Note: notes += j.line + "\n"; break;
        default: break;
        }
    if (!notes.empty())
        if (FILE* f = _wfopen((g_session / L"session.txt").c_str(), L"a")) { fputs(notes.c_str(), f); fclose(f); }
    for (FILE* f : { g_pipelines, g_refused, g_timing }) if (f) fflush(f);
}

void WriterLoop()
{
    std::vector<Job> batch;
    while (true)
    {
        {
            std::unique_lock g(g_queueLock);
            g_queueReady.wait(g, [] { return !g_queue.empty(); });
            batch.swap(g_queue);
            g_saving = true;
        }
        Save(batch);
        batch.clear();
        g_saving = false;
    }
}

/// When the game is closing: whatever is still queued is saved on the closing thread. Windows has already stopped the
/// writer thread by then, perhaps in the middle of saving (holding a lock or a file): then nothing more is written,
/// because waiting for it would hang the game's exit. At worst the last moment's lines are lost.
void SaveRemaining()
{
    if (g_saving || !g_queueLock.try_lock()) return;
    std::vector<Job> batch;
    batch.swap(g_queue);
    g_queueLock.unlock();
    if (!batch.empty()) Save(batch);
}

// ---------------------------------------------------------------- recording

void Note(const std::string& line) { Enqueue({ Job::Kind::Note, 0, RecordKind::Graphics, {}, line }); }

template <class T> void Patch(void** table, int index, T hook, T& original)
{
    DWORD old;
    VirtualProtect(&table[index], sizeof(void*), PAGE_READWRITE, &old);
    original = reinterpret_cast<T>(table[index]);
    table[index] = reinterpret_cast<void*>(hook);
    VirtualProtect(&table[index], sizeof(void*), old, &old);
}

// Called with g_lock held. A shader seen for the first time is copied into the queue; its hash names it.
uint64_t StoreBlob(const void* data, size_t size)
{
    uint64_t h = Hash(data, size);
    if (h <= kUnknownRootSignature) h += 2;   // 0 and 1 mean "none" and "unknown" in a record
    if (g_blobs.insert(h).second)
    {
        auto p = static_cast<const uint8_t*>(data);
        Enqueue({ Job::Kind::Blob, h, RecordKind::Graphics, std::vector<uint8_t>(p, p + size), {} });
    }
    return h;
}

uint64_t RootSignatureRef(ID3D12RootSignature* rs)
{
    if (!rs) return kNoRootSignature;
    auto it = g_rootSignatures.find(rs);
    return it == g_rootSignatures.end() ? kUnknownRootSignature : it->second;
}

/// Reads a description the driver refused. It may hold bad pointers (that can be why it was refused), so a fault while
/// reading it is caught and the description is skipped instead of taking the game down.
template <class WriteFn> bool GuardedWrite(WriteFn& write, Writer& w, const BlobSink& sink)
{
    __try { return write(w, sink); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

/// Records one creation: the timing line always; the pipeline itself the first time it's seen (in pipelines.bin when it
/// worked, in refused.bin when the driver refused it: those are never replayed, only counted by the report).
template <class WriteFn>
void Record(const char* kindName, RecordKind kind, double ms, HRESULT hr, ID3D12Device* device, WriteFn write)
{
    if (!g_recording) return;
    const double entered = NowMs();   // for rec_ms: what recording this call cost the game's thread
    std::string adapterNote;
    LUID luid = device->GetAdapterLuid();
    uint64_t key = (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
    uint64_t id = 0;
    bool isNew = false;
    double t;
    {
        std::lock_guard g(g_lock);
        t = NowMs() - g_start;
        BlobSink sink{ StoreBlob, RootSignatureRef };
        Writer w;
        if (SUCCEEDED(hr) ? write(w, sink) : GuardedWrite(write, w, sink))
        {
            id = Hash(w.bytes.data(), w.bytes.size());
            isNew = (SUCCEEDED(hr) ? g_records : g_refusedIds).insert(id).second;
            if (isNew) Enqueue({ SUCCEEDED(hr) ? Job::Kind::Record : Job::Kind::Refused, id, kind, std::move(w.bytes), {} });
        }
        // Which card this creation was on, described once by its PCI identity (which survives a restart).
        if (g_luids.insert(key).second) adapterNote = AdapterLine(DescribeAdapter(luid));
    }
    if (!adapterNote.empty()) Note(adapterNote);
    char line[256];
    sprintf_s(line, "%.3f,%s,%.3f,%08lx,%s,%d,%lu,%s,%.3f\n", t, kindName, ms, static_cast<unsigned long>(hr), id ? Hex(id).c_str() : "",
              isNew ? 1 : 0, GetCurrentThreadId(), LuidText(luid).c_str(), NowMs() - entered);
    Enqueue({ Job::Kind::Timing, 0, kind, {}, line });
}

ID3D12Device* DeviceOf(ID3D12DeviceChild* child)
{
    ComPtr<ID3D12Device> d;
    child->GetDevice(IID_PPV_ARGS(&d));
    return d.Get();   // the device outlives this call (the child holds a reference)
}

// ---------------------------------------------------------------- the hooks

HRESULT STDMETHODCALLTYPE HkGraphics(ID3D12Device* d, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_gfx(d, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("graphics", RecordKind::Graphics, ms, hr, d, [&](Writer& w, const BlobSink& s) { WriteGraphics(w, *desc, s); return true; });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkCompute(ID3D12Device* d, const D3D12_COMPUTE_PIPELINE_STATE_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_cmp(d, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("compute", RecordKind::Compute, ms, hr, d, [&](Writer& w, const BlobSink& s) { WriteCompute(w, *desc, s); return true; });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkStream(ID3D12Device2* d, const D3D12_PIPELINE_STATE_STREAM_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_stream(d, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("stream", RecordKind::Stream, ms, hr, d, [&](Writer& w, const BlobSink& s) { return WriteStream(w, *desc, s); });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkRootSignature(ID3D12Device* d, UINT nodeMask, const void* blob, SIZE_T size, REFIID riid, void** out)
{
    HRESULT hr = o_rs(d, nodeMask, blob, size, riid, out);
    if (SUCCEEDED(hr) && out && *out && blob && size && g_recording)
    {
        // The only interface a root signature has is ID3D12RootSignature, so *out is the object whatever riid asked for.
        std::lock_guard g(g_lock);
        g_rootSignatures[static_cast<ID3D12RootSignature*>(*out)] = StoreBlob(blob, size);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HkLibGraphics(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_libGfx(lib, name, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("library-graphics", RecordKind::Graphics, ms, hr, DeviceOf(lib), [&](Writer& w, const BlobSink& s) { WriteGraphics(w, *desc, s); return true; });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkLibCompute(ID3D12PipelineLibrary* lib, LPCWSTR name, const D3D12_COMPUTE_PIPELINE_STATE_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_libCmp(lib, name, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("library-compute", RecordKind::Compute, ms, hr, DeviceOf(lib), [&](Writer& w, const BlobSink& s) { WriteCompute(w, *desc, s); return true; });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkLibStream(ID3D12PipelineLibrary1* lib, LPCWSTR name, const D3D12_PIPELINE_STATE_STREAM_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_libStream(lib, name, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out) Record("library-stream", RecordKind::Stream, ms, hr, DeviceOf(lib), [&](Writer& w, const BlobSink& s) { return WriteStream(w, *desc, s); });
    return hr;
}

HRESULT STDMETHODCALLTYPE HkPipelineLibrary(ID3D12Device1* d, const void* blob, SIZE_T size, REFIID riid, void** out)
{
    HRESULT hr = o_lib(d, blob, size, riid, out);
    if (SUCCEEDED(hr) && out && *out)
    {
        // All pipeline libraries share one table too; swap its load functions the first time one is made.
        ComPtr<IUnknown> unknown(static_cast<IUnknown*>(*out));
        ComPtr<ID3D12PipelineLibrary> lib;
        if (SUCCEEDED(unknown.As(&lib)))
            std::call_once(g_libraryPatched, [&]
            {
                void** table = *reinterpret_cast<void***>(lib.Get());
                Patch(table, 9, &HkLibGraphics, o_libGfx);
                Patch(table, 10, &HkLibCompute, o_libCmp);
                ComPtr<ID3D12PipelineLibrary1> lib1;
                if (SUCCEEDED(lib.As(&lib1))) Patch(table, 13, &HkLibStream, o_libStream);
                Note("pipeline_library\tfirst one created; its load functions are recorded too");
            });
    }
    return hr;
}

/// Ray tracing pipelines (and other state objects) aren't recorded; each creation gets a timing line, so the report
/// shows how many the game built and how long they took.
HRESULT STDMETHODCALLTYPE HkStateObject(ID3D12Device5* d, const D3D12_STATE_OBJECT_DESC* desc, REFIID riid, void** out)
{
    double t0 = NowMs();
    HRESULT hr = o_stateObject(d, desc, riid, out);
    double ms = NowMs() - t0;
    if (desc && out)
    {
        const char* kind = desc->Type == D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE ? "stateobject-raytracing"
                         : desc->Type == D3D12_STATE_OBJECT_TYPE_COLLECTION ? "stateobject-collection" : "stateobject-other";
        Record(kind, RecordKind::Graphics, ms, hr, d, [](Writer&, const BlobSink&) { return false; });   // a timing line only
    }
    return hr;
}

/// A game asks this before building a ray tracing scene: a sign ray tracing is in use. Noted once; not timed (it can be
/// called every frame).
void STDMETHODCALLTYPE HkPrebuild(ID3D12Device5* d, const D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS* in,
                                  D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO* info)
{
    o_prebuild(d, in, info);
    if (g_recording && !g_sceneNoted.exchange(true))
        Note("raytracing_scene\tthe game started building ray tracing scenes " + std::to_string(static_cast<long long>(NowMs() - g_start)) +
             " ms after the hooks went in");
}

// ---------------------------------------------------------------- start-up

FILE* OpenRecordFile(const fs::path& path)
{
    FILE* f = _wfopen(path.c_str(), L"wb");
    if (f) { fwrite(kRecordMagic, 1, sizeof(kRecordMagic), f); fflush(f); }
    return f;
}

DWORD WINAPI Start(void*)
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    const fs::path root = DataRoot() / fs::path(exe).filename();
    g_blobDir = root / L"blobs";
    g_session = root / L"recordings" / Widen(Stamp());
    std::error_code ec;
    fs::create_directories(g_blobDir, ec);
    fs::create_directories(g_session, ec);
    // The session notes are written straight away (the writer isn't running yet).
    if (FILE* f = _wfopen((g_session / L"session.txt").c_str(), L"a"))
    {
        fprintf(f, "program\t%s\nprocess_id\t%lu\nstarted\t%s\nrecorder\tupshift_rec 3 (Upshift: background writes)\n",
                Narrow(exe).c_str(), GetCurrentProcessId(), Stamp().c_str());
        fclose(f);
    }

    HMODULE d3d12 = LoadLibraryW(L"d3d12.dll");
    auto create = d3d12 ? reinterpret_cast<PFN_D3D12_CREATE_DEVICE>(GetProcAddress(d3d12, "D3D12CreateDevice")) : nullptr;
    auto fail = [](const char* why) { if (FILE* f = _wfopen((g_session / L"session.txt").c_str(), L"a")) { fprintf(f, "error\t%s\n", why); fclose(f); } return 0; };
    if (!create) return fail("d3d12.dll couldn't be loaded; nothing is recorded");
    // Its own small device, to find the shared function table. 1_0_CORE first (OptiScaler doesn't touch those).
    if (FAILED(create(nullptr, D3D_FEATURE_LEVEL_1_0_CORE, IID_PPV_ARGS(&g_ownDevice))) &&
        FAILED(create(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_ownDevice))))
        return fail("couldn't create a Direct3D 12 device; nothing is recorded");

    g_pipelines = OpenRecordFile(g_session / L"pipelines.bin");
    g_refused = OpenRecordFile(g_session / L"refused.bin");
    g_timing = _wfopen((g_session / L"timing.csv").c_str(), L"w");
    if (!g_pipelines || !g_timing) return fail("couldn't create the recording files");
    fprintf(g_timing, "t_ms,kind,ms,result,record_id,new,thread,adapter,rec_ms\n");
    fflush(g_timing);
    std::thread(WriterLoop).detach();
    g_start = NowMs();

    void** table = *reinterpret_cast<void***>(g_ownDevice.Get());
    Patch(table, 16, &HkRootSignature, o_rs);
    Patch(table, 10, &HkGraphics, o_gfx);
    Patch(table, 11, &HkCompute, o_cmp);
    Patch(table, 44, &HkPipelineLibrary, o_lib);
    Patch(table, 47, &HkStream, o_stream);
    Patch(table, 62, &HkStateObject, o_stateObject);
    Patch(table, 63, &HkPrebuild, o_prebuild);
    g_recording = true;   // last: the hooks start recording once this is set
    Note("hooks\tin place (times in timing.csv count from here)");
    // Which Direct3D 12 runtime the swapped table belongs to (a game or mod that loads a second runtime would bypass it).
    HMODULE core = nullptr;
    wchar_t corePath[MAX_PATH] = L"unknown";
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(o_gfx), &core))
        GetModuleFileNameW(core, corePath, MAX_PATH);
    Note("d3d12_runtime\t" + Narrow(corePath));
    return 0;
}
} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        // Nothing heavy under the loader lock: the work happens on a thread, which runs once the loader is done.
        if (HANDLE t = CreateThread(nullptr, 0, Start, nullptr, 0, nullptr)) CloseHandle(t);
    }
    else if (reason == DLL_PROCESS_DETACH && g_recording)
    {
        // The game is closing and the writer thread has already been stopped by Windows: save what's left.
        g_recording = false;
        SaveRemaining();
    }
    return TRUE;
}
