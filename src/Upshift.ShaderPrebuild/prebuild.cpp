// upshift_prebuild.exe: rebuilds every pipeline Upshift learned for a game, so the graphics driver has them in its shader
// cache before the game asks for them ("Pre-build shaders", experimental).
//
//   upshift_prebuild.exe --prebuild <game program, e.g. re9.exe> --dir <the game's folder in Upshift's data>
//     Refuses while that program is running. Copies itself under the game's program name into
//     <data>\shaderwarm\replay\<name>\ (the drivers tie their cache to the program name), runs that copy, and deletes it
//     afterwards. The copy rebuilds the pipelines on this PC's graphics card from the library Upshift merged for that card.
//   upshift_prebuild.exe --worker --dir <folder>   the copy (internal)
//
// Output, one line each, for Upshift to read:
//   card <name> \t <how it matched>
//   progress <done> <total> <failed>
//   result <built> <failed> <seconds> <threads> <different>   (different: rebuilt from a description that doesn't
//                                                               hash back to its record: a bug, and reported)
//   error <message>
// The library files (written by Upshift, library\<card model>.bin):
//   "USWLIB1\0", u32 header size, the header (UTF-8 "model\t<vendor> <device> <subvendor> <subsystem> <revision>" and
//   "name\t<card>" lines), then records exactly as in a recording's pipelines.bin: u32 kind, u64 id, u32 size, payload.
#include "adapter.h"
#include "recording.h"

#include <dxgi1_6.h>
#include <tlhelp32.h>

#include <algorithm>
#include <atomic>
#include <iostream>
#include <map>
#include <mutex>
#include <thread>
#include <unordered_map>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "gdi32.lib")

using Microsoft::WRL::ComPtr;
using namespace warm;

namespace
{
using Args = std::vector<std::wstring>;

std::wstring Value(const Args& args, const wchar_t* name)
{
    for (size_t i = 0; i + 1 < args.size(); ++i) if (args[i] == name) return args[i + 1];
    return {};
}

void Line(const std::string& s) { std::cout << s << "\n" << std::flush; }

int Error(const std::string& why)
{
    Line("error " + why);
    return 1;
}

bool IsRunning(const std::wstring& exe)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;
    PROCESSENTRY32W pe{ sizeof(pe) };
    bool found = false;
    DWORD self = GetCurrentProcessId();
    for (BOOL ok = Process32FirstW(snap, &pe); ok && !found; ok = Process32NextW(snap, &pe))
        found = pe.th32ProcessID != self && _wcsicmp(pe.szExeFile, exe.c_str()) == 0;
    CloseHandle(snap);
    return found;
}

fs::path SelfPath()
{
    wchar_t buf[MAX_PATH];
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    return buf;
}

std::string Fixed(double v, int digits = 1)
{
    std::ostringstream o;
    o << std::fixed << std::setprecision(digits) << v;
    return o.str();
}

// ---------------------------------------------------------------- the library

constexpr char kLibraryMagic[8] = { 'U', 'S', 'W', 'L', 'I', 'B', '1', '\0' };

struct Library
{
    fs::path file;
    AdapterId model;   // vendor, device, subsystem, revision (no LUID)
    std::string name;
};

std::vector<Library> Libraries(const fs::path& gameDir)
{
    std::vector<Library> out;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(gameDir / L"library", ec))
    {
        if (e.path().extension() != L".bin") continue;
        std::ifstream in(e.path(), std::ios::binary);
        char magic[8]{};
        uint32_t size = 0;
        if (!in.read(magic, 8) || std::memcmp(magic, kLibraryMagic, 8) != 0 || !in.read(reinterpret_cast<char*>(&size), 4) || size > 65536) continue;
        std::string header(size, '\0');
        if (!in.read(header.data(), size)) continue;
        Library lib{ e.path() };
        std::istringstream lines(header);
        for (std::string l; std::getline(lines, l);)
        {
            if (l.rfind("model\t", 0) == 0)
                lib.model.known = sscanf_s(l.c_str() + 6, "%x %x %x %x %x", &lib.model.vendor, &lib.model.device, &lib.model.subVendor,
                                           &lib.model.subSystem, &lib.model.revision) == 5;
            else if (l.rfind("name\t", 0) == 0) lib.name = l.substr(5);
        }
        if (lib.model.known) out.push_back(lib);
    }
    return out;
}

std::vector<StoredRecord> ReadLibrary(const fs::path& file)
{
    std::vector<StoredRecord> out;
    std::ifstream in(file, std::ios::binary);
    char magic[8]{};
    uint32_t size = 0;
    if (!in.read(magic, 8) || std::memcmp(magic, kLibraryMagic, 8) != 0 || !in.read(reinterpret_cast<char*>(&size), 4)) return out;
    in.seekg(size, std::ios::cur);
    while (true)
    {
        uint32_t kind = 0, n = 0;
        uint64_t id = 0;
        if (!in.read(reinterpret_cast<char*>(&kind), 4) || !in.read(reinterpret_cast<char*>(&id), 8) || !in.read(reinterpret_cast<char*>(&n), 4)) break;
        if (n > 64 * 1024 * 1024) break;
        StoredRecord r{ static_cast<RecordKind>(kind), id, std::vector<uint8_t>(n) };
        if (n && !in.read(reinterpret_cast<char*>(r.payload.data()), n)) break;
        out.push_back(std::move(r));
    }
    return out;
}

// ---------------------------------------------------------------- which card

/// The real graphics cards in this PC now: never a software renderer.
std::vector<std::pair<ComPtr<IDXGIAdapter1>, AdapterId>> HardwareCards()
{
    std::vector<std::pair<ComPtr<IDXGIAdapter1>, AdapterId>> out;
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return out;
    ComPtr<IDXGIAdapter1> a;
    for (UINT i = 0; factory->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i, a.Reset())
    {
        DXGI_ADAPTER_DESC1 d;
        if (FAILED(a->GetDesc1(&d)) || (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)) continue;
        AdapterId id = DescribeAdapter(d.AdapterLuid);   // its PCI identity from Windows' kernel graphics interface, not DXGI
        if (id.software || !id.known) continue;
        if (id.name.empty()) id.name = d.Description;
        out.emplace_back(a, id);
    }
    return out;
}

// ---------------------------------------------------------------- the copy that rebuilds

int RunWorker(const Args& args)
{
    const fs::path gameDir = Value(args, L"--dir");
    const auto libraries = Libraries(gameDir);
    if (libraries.empty()) return Error("no shaders have been learned for this game yet");

    // The card: one in this PC whose exact model has a library; otherwise the same chip (vendor and device) on another
    // board. The largest library wins when there are several.
    const auto cards = HardwareCards();
    const Library* lib = nullptr;
    const std::pair<ComPtr<IDXGIAdapter1>, AdapterId>* card = nullptr;
    std::string how;
    for (int pass = 0; pass < 2 && !lib; ++pass)
        for (auto& l : libraries)
            for (auto& c : cards)
            {
                bool match = pass == 0 ? c.second.SameModel(l.model) : (c.second.vendor == l.model.vendor && c.second.device == l.model.device);
                if (!match) continue;
                std::error_code ec;
                if (!lib || fs::file_size(l.file, ec) > fs::file_size(lib->file, ec)) { lib = &l; card = &c; how = pass == 0 ? "the same model" : "the same chip on another board"; }
            }
    if (!lib)
    {
        std::string learned;
        for (auto& l : libraries) learned += (learned.empty() ? "" : ", ") + l.name;
        return Error("the shaders were learned on " + learned + ", which isn't in this PC. Play the game with learning on to learn them for this card.");
    }
    Line("card " + Narrow(card->second.name) + "\t" + how);

    ComPtr<ID3D12Device2> device;
    if (FAILED(D3D12CreateDevice(card->first.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device))))
        return Error("Direct3D 12 couldn't start on " + Narrow(card->second.name) + ".");
    if (!(device->GetAdapterLuid() == card->second.luid)) return Error("Direct3D 12 started on a different card than the one chosen.");

    auto records = ReadLibrary(lib->file);
    if (records.empty()) return Error("the library for this card is empty");

    // Shader files and root signatures, loaded when first needed and shared by the threads.
    std::mutex lock;
    std::unordered_map<uint64_t, std::unique_ptr<std::vector<uint8_t>>> blobs;
    std::unordered_map<uint64_t, ComPtr<ID3D12RootSignature>> rootSignatures;
    auto blob = [&](uint64_t h) -> const std::vector<uint8_t>*
    {
        std::lock_guard g(lock);
        auto it = blobs.find(h);
        if (it == blobs.end())
        {
            std::unique_ptr<std::vector<uint8_t>> data;
            std::ifstream in(gameDir / L"blobs" / Widen(Hex(h) + ".bin"), std::ios::binary);
            if (in) data = std::make_unique<std::vector<uint8_t>>(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
            it = blobs.emplace(h, std::move(data)).first;
        }
        return it->second.get();
    };
    BlobSource source;
    source.blob = blob;
    source.rootSignature = [&](uint64_t h) -> ID3D12RootSignature*
    {
        {
            std::lock_guard g(lock);
            if (auto it = rootSignatures.find(h); it != rootSignatures.end()) return it->second.Get();
        }
        ComPtr<ID3D12RootSignature> rs;
        if (auto* b = blob(h)) device->CreateRootSignature(0, b->data(), b->size(), IID_PPV_ARGS(&rs));
        std::lock_guard g(lock);
        return rootSignatures.emplace(h, rs).first->second.Get();
    };

    // The check that a rebuilt description is exactly what the game passed: written out again, it must hash to the same
    // record id (shaders back to their hashes, the root signature to the file it was rebuilt from).
    auto sameAsRecorded = [&](const StoredRecord& rec, Rebuilt& p)
    {
        BlobSink resink;
        resink.store = [](const void* data, size_t n) { uint64_t h = Hash(data, n); return h <= kUnknownRootSignature ? h + 2 : h; };
        resink.rootSignature = [&](ID3D12RootSignature* rs) { return rs ? p.rootSignatureRef : kNoRootSignature; };
        Writer w;
        if (p.kind == RecordKind::Graphics) WriteGraphics(w, p.graphics, resink);
        else if (p.kind == RecordKind::Compute) WriteCompute(w, p.compute, resink);
        else WriteStream(w, { p.stream.size(), p.stream.data() }, resink);
        return Hash(w.bytes.data(), w.bytes.size()) == rec.id;
    };

    const int threads = std::clamp(static_cast<int>(std::thread::hardware_concurrency()) - 2, 2, 16);
    std::atomic<size_t> next{ 0 }, done{ 0 }, failed{ 0 }, different{ 0 };
    std::mutex reasonsLock;
    std::map<std::string, size_t> reasons;
    double slowest = 0;
    const double start = NowMs();
    std::vector<std::thread> pool;
    for (int t = 0; t < threads; ++t)
        pool.emplace_back([&]
        {
            for (size_t i; (i = next++) < records.size();)
            {
                std::string problem;
                double ms = 0;
                auto p = Rebuild(records[i].kind, records[i].payload.data(), records[i].payload.size(), source);
                if (p->problem.empty() && !sameAsRecorded(records[i], *p)) ++different;
                if (!p->problem.empty()) problem = p->problem;
                else if (HRESULT hr = CreateRebuilt(device.Get(), *p, ms); FAILED(hr))
                {
                    char buf[64];
                    sprintf_s(buf, "the driver refused it (0x%08lx)", static_cast<unsigned long>(hr));
                    problem = buf;
                }
                {
                    std::lock_guard g(reasonsLock);
                    if (!problem.empty()) ++reasons[problem];
                    else slowest = std::max(slowest, ms);
                }
                if (!problem.empty()) ++failed;
                ++done;
            }
        });
    while (done < records.size())
    {
        Line("progress " + std::to_string(done.load()) + " " + std::to_string(records.size()) + " " + std::to_string(failed.load()));
        Sleep(250);
    }
    for (auto& t : pool) t.join();
    const double seconds = (NowMs() - start) / 1000.0;
    Line("progress " + std::to_string(records.size()) + " " + std::to_string(records.size()) + " " + std::to_string(failed.load()));

    // A summary next to the learned shaders: the report uses its time to mark the next session as "after a pre-build".
    std::ostringstream s;
    s << "prebuild\t" << Stamp() << "\ncard\t" << Narrow(card->second.name) << "\t" << ModelText(card->second) << "\nbuilt\t"
      << records.size() - failed << "\nfailed\t" << failed.load() << "\nseconds\t" << Fixed(seconds, 1) << "\nthreads\t" << threads
      << "\nslowest_ms\t" << Fixed(slowest, 1) << "\ndifferent\t" << different.load() << "\n";
    for (auto& [why, n] : reasons) s << "reason\t" << n << "\t" << why << "\n";
    std::error_code ec;
    fs::create_directories(gameDir / L"replays", ec);
    std::ofstream(gameDir / L"replays" / Widen(Stamp() + ".txt")) << s.str();

    Line("result " + std::to_string(records.size() - failed) + " " + std::to_string(failed.load()) + " " + Fixed(seconds, 1) + " " + std::to_string(threads) + " " +
         std::to_string(different.load()));
    return failed == records.size() ? 1 : 0;
}

// ---------------------------------------------------------------- the starter

int RunPrebuild(const Args& args)
{
    std::wstring exe = fs::path(Value(args, L"--prebuild")).filename().wstring();
    const fs::path gameDir = Value(args, L"--dir");
    if (exe.empty() || gameDir.empty()) return Error("usage: --prebuild <game program> --dir <folder>");
    if (exe.size() < 4 || _wcsicmp(exe.c_str() + exe.size() - 4, L".exe") != 0) exe += L".exe";
    if (IsRunning(exe)) return Error(Narrow(exe) + " is running. Close the game first.");

    // A copy of this program under the game's name, in Upshift's data folder (never the game folder).
    const fs::path tempDir = gameDir.parent_path() / L"replay" / fs::path(exe).stem();
    const fs::path copy = tempDir / exe;
    std::error_code ec;
    fs::create_directories(tempDir, ec);
    if (!fs::copy_file(SelfPath(), copy, fs::copy_options::overwrite_existing, ec))
        return Error("couldn't copy the pre-build program to " + Narrow(copy.wstring()) + ": " + ec.message());

    std::wstring cmd = L"\"" + copy.wstring() + L"\" --worker --dir \"" + gameDir.wstring() + L"\"";
    STARTUPINFOW si{ sizeof(si) };
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi{};
    DWORD code = 1;
    if (!CreateProcessW(copy.c_str(), cmd.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, tempDir.c_str(), &si, &pi))
        Line("error couldn't start the pre-build (Windows error " + std::to_string(GetLastError()) + ")");
    else
    {
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, &code);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
    fs::remove_all(tempDir, ec);   // only the copy made above
    return static_cast<int>(code);
}
} // namespace

int wmain(int argc, wchar_t** argv)
{
    const Args args(argv + 1, argv + argc);
    const std::wstring first = args.empty() ? L"" : args[0];
    if (first == L"--worker") return RunWorker(args);
    if (first == L"--prebuild") return RunPrebuild(args);
    return Error("usage: upshift_prebuild.exe --prebuild <game program> --dir <folder>");
}
