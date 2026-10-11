// Which graphics card a pipeline was built on, described in a way that survives a restart.
//
// A LUID (what Direct3D 12 reports for a device) is only valid until the PC restarts, so the recorder also saves the
// card's PCI identity: vendor, device, subsystem and revision. It reads them from Windows' kernel graphics interface
// (D3DKMTQueryAdapterInfo, documented in Microsoft's d3dkmthk.h), not from DXGI: OptiScaler is the game's dxgi.dll and
// can make an AMD or Intel card look like an NVIDIA one through DXGI. The replayer reads them the same way.
#pragma once
#include "common.h"

#include <winternl.h>
#include <d3dkmthk.h>

#include <cstdio>
#include <vector>

namespace warm
{
struct AdapterId
{
    LUID luid{};
    bool known = false;            // the details below could be read
    uint32_t vendor = 0, device = 0, subVendor = 0, subSystem = 0, revision = 0;
    bool software = false;         // Microsoft Basic Render Driver or any other software renderer
    std::wstring name;             // the driver's name for the card (from the registry, not DXGI)

    /// The same physical model: what still matches after a restart.
    bool SameModel(const AdapterId& o) const
    {
        return known && o.known && vendor == o.vendor && device == o.device && subVendor == o.subVendor && subSystem == o.subSystem &&
               revision == o.revision;
    }
};

inline std::string LuidText(LUID l)
{
    char buf[32];
    sprintf_s(buf, "%08lx:%08lx", static_cast<unsigned long>(l.HighPart), static_cast<unsigned long>(l.LowPart));
    return buf;
}

inline bool ParseLuid(const std::string& s, LUID& l)
{
    unsigned long hi = 0, lo = 0;
    if (sscanf_s(s.c_str(), "%lx:%lx", &hi, &lo) != 2) return false;
    l.HighPart = static_cast<LONG>(hi);
    l.LowPart = lo;
    return true;
}

inline bool operator==(const LUID& a, const LUID& b) { return a.HighPart == b.HighPart && a.LowPart == b.LowPart; }

/// The card behind a LUID, now. known = false when no adapter has that LUID (e.g. after a restart).
inline AdapterId DescribeAdapter(LUID luid)
{
    AdapterId id;
    id.luid = luid;
    D3DKMT_OPENADAPTERFROMLUID open{};
    open.AdapterLuid = luid;
    if (D3DKMTOpenAdapterFromLuid(&open) != 0) return id;
    auto query = [&](KMTQUERYADAPTERINFOTYPE type, void* data, UINT size)
    {
        D3DKMT_QUERYADAPTERINFO q{};
        q.hAdapter = open.hAdapter;
        q.Type = type;
        q.pPrivateDriverData = data;
        q.PrivateDriverDataSize = size;
        return D3DKMTQueryAdapterInfo(&q) == 0;
    };
    D3DKMT_QUERY_DEVICE_IDS ids{};
    ids.PhysicalAdapterIndex = 0;
    if (query(KMTQAITYPE_PHYSICALADAPTERDEVICEIDS, &ids, sizeof(ids)))
    {
        id.known = true;
        id.vendor = ids.DeviceIds.VendorID;
        id.device = ids.DeviceIds.DeviceID;
        id.subVendor = ids.DeviceIds.SubVendorID;
        id.subSystem = ids.DeviceIds.SubSystemID;
        id.revision = ids.DeviceIds.RevisionID;
    }
    D3DKMT_ADAPTERTYPE type{};
    if (query(KMTQAITYPE_ADAPTERTYPE, &type, sizeof(type))) id.software = type.SoftwareDevice != 0;
    // Microsoft Basic Render Driver: vendor 0x1414, device 0x8C (in case the type query isn't answered).
    if (id.vendor == 0x1414 && id.device == 0x8C) id.software = true;
    D3DKMT_ADAPTERREGISTRYINFO reg{};
    if (query(KMTQAITYPE_ADAPTERREGISTRYINFO, &reg, sizeof(reg))) id.name = reg.AdapterString;
    if (id.name.empty() && id.vendor == 0x1414 && id.device == 0x8C) id.name = L"Microsoft Basic Render Driver";
    if (id.name.empty()) id.name = id.software ? L"a software renderer" : L"unnamed graphics card";
    D3DKMT_CLOSEADAPTER close{ open.hAdapter };
    D3DKMTCloseAdapter(&close);
    return id;
}

/// One line for session.txt: "adapter <tab> luid <tab> vendor device subvendor subsystem revision (hex) <tab> software <tab> name".
inline std::string AdapterLine(const AdapterId& a)
{
    char ids[64];
    sprintf_s(ids, "%04x %04x %04x %04x %02x", a.vendor, a.device, a.subVendor, a.subSystem, a.revision);
    return "adapter\t" + LuidText(a.luid) + "\t" + (a.known ? ids : "unknown") + "\t" + (a.software ? "1" : "0") + "\t" + Narrow(a.name);
}

/// Reads AdapterLine back (false for other lines).
inline bool ParseAdapterLine(const std::string& line, AdapterId& a)
{
    if (line.rfind("adapter\t", 0) != 0) return false;
    std::vector<std::string> f;
    size_t start = 0;
    for (size_t tab; (tab = line.find('\t', start)) != std::string::npos; start = tab + 1) f.push_back(line.substr(start, tab - start));
    f.push_back(line.substr(start));
    if (f.size() < 5 || !ParseLuid(f[1], a.luid)) return false;
    a.known = sscanf_s(f[2].c_str(), "%x %x %x %x %x", &a.vendor, &a.device, &a.subVendor, &a.subSystem, &a.revision) == 5;
    a.software = f[3] == "1";
    a.name = Widen(f[4]);
    return true;
}

inline std::string ModelText(const AdapterId& a)
{
    char buf[96];
    sprintf_s(buf, "PCI %04X:%04X, subsystem %04X:%04X, revision %02X", a.vendor, a.device, a.subVendor, a.subSystem, a.revision);
    return a.known ? buf : "no details";
}
} // namespace warm
