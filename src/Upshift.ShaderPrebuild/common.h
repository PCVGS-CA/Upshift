// Small helpers shared by upshift_warm.exe and the recorder (upshift_rec.asi). Windows and Direct3D 12 only.
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>

#include <cstdint>
#include <filesystem>
#include <iomanip>
#include <sstream>
#include <string>

namespace warm
{
namespace fs = std::filesystem;

inline double NowMs()
{
    static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return 1000.0 * static_cast<double>(t.QuadPart) / static_cast<double>(freq.QuadPart);
}

inline std::string Narrow(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

inline std::wstring Widen(const std::string& s)
{
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

/// 64-bit FNV-1a: names shader and root-signature blobs and identifies pipeline records.
inline uint64_t Hash(const void* data, size_t size, uint64_t h = 0xcbf29ce484222325ull)
{
    auto p = static_cast<const uint8_t*>(data);
    for (size_t i = 0; i < size; ++i) { h ^= p[i]; h *= 0x100000001b3ull; }
    return h;
}

inline std::string Hex(uint64_t v)
{
    std::ostringstream o;
    o << std::hex << std::setw(16) << std::setfill('0') << v;
    return o.str();
}

/// Upshift's data folder for this experiment: %LOCALAPPDATA%\Upshift\shaderwarm.
inline fs::path DataRoot()
{
    PWSTR path = nullptr;
    fs::path root;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &path))) root = fs::path(path) / L"Upshift" / L"shaderwarm";
    CoTaskMemFree(path);
    return root;
}

/// The local time as yyyyMMdd-HHmmss (session folder names).
inline std::string Stamp()
{
    SYSTEMTIME t;
    GetLocalTime(&t);
    char buf[32];
    sprintf_s(buf, "%04u%02u%02u-%02u%02u%02u", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return buf;
}
} // namespace warm
