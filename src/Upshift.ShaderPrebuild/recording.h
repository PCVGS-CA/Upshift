// The recording format, shared by the recorder (which writes it inside the game) and the replayer (which rebuilds the
// pipelines from it). Everything needed to call the same Direct3D 12 pipeline creation again: the full settings, with
// every pointer replaced by data. Shader bytecode and root signatures are stored once each, as files named by their
// hash, under blobs\. Layouts follow Microsoft's d3d12.h; pipeline state streams follow the documented subobject layout
// (each subobject starts pointer-aligned: a 4-byte type, then the inner struct at its natural alignment).
//
// Session folder (in Upshift's data folder, never the game folder):
//   session.txt     game program, adapter LUID, when the hooks went in
//   pipelines.bin   "USWREC1\0", then records: u32 kind, u64 id, u32 size, payload (each distinct pipeline once)
//   timing.csv      every creation the game made: time, kind, driver time, result, record id, thread
//   blobs\<hash>.bin  shader bytecode and serialized root signatures
#pragma once
#include "common.h"

#include <d3d12.h>
#include <wrl/client.h>

#include <cstring>
#include <deque>
#include <fstream>
#include <functional>
#include <memory>
#include <optional>
#include <vector>

namespace warm
{
enum class RecordKind : uint32_t { Graphics = 1, Compute = 2, Stream = 3 };

/// Root signature references in a record: 0 = none given, 1 = one the recorder didn't see being created.
constexpr uint64_t kNoRootSignature = 0, kUnknownRootSignature = 1;

// ---------------------------------------------------------------- writing

class Writer
{
public:
    std::vector<uint8_t> bytes;
    void U8(uint8_t v) { bytes.push_back(v); }
    void U32(uint32_t v) { Raw(&v, 4); }
    void U64(uint64_t v) { Raw(&v, 8); }
    void Raw(const void* p, size_t n) { auto b = static_cast<const uint8_t*>(p); bytes.insert(bytes.end(), b, b + n); }
    void Str(const char* s) { size_t n = s ? std::strlen(s) : 0; U32(static_cast<uint32_t>(n)); Raw(s, n); }
    template <class T> void Pod(const T& v) { Raw(&v, sizeof(T)); }
};

/// What the writer needs from its owner: store a blob (once) and return its hash, and look up a root signature.
struct BlobSink
{
    std::function<uint64_t(const void*, size_t)> store;
    std::function<uint64_t(ID3D12RootSignature*)> rootSignature;
};

inline void WriteShader(Writer& w, const D3D12_SHADER_BYTECODE& s, const BlobSink& sink)
{
    w.U64(s.pShaderBytecode && s.BytecodeLength ? sink.store(s.pShaderBytecode, s.BytecodeLength) : 0);
}

inline void WriteStreamOutput(Writer& w, const D3D12_STREAM_OUTPUT_DESC& so)
{
    w.U32(so.pSODeclaration ? so.NumEntries : 0);
    for (UINT i = 0; so.pSODeclaration && i < so.NumEntries; ++i)
    {
        const auto& e = so.pSODeclaration[i];
        w.U32(e.Stream); w.Str(e.SemanticName); w.U32(e.SemanticIndex); w.U8(e.StartComponent); w.U8(e.ComponentCount); w.U8(e.OutputSlot);
    }
    w.U32(so.pBufferStrides ? so.NumStrides : 0);
    for (UINT i = 0; so.pBufferStrides && i < so.NumStrides; ++i) w.U32(so.pBufferStrides[i]);
    w.U32(so.RasterizedStream);
}

inline void WriteInputLayout(Writer& w, const D3D12_INPUT_LAYOUT_DESC& il)
{
    w.U32(il.pInputElementDescs ? il.NumElements : 0);
    for (UINT i = 0; il.pInputElementDescs && i < il.NumElements; ++i)
    {
        const auto& e = il.pInputElementDescs[i];
        w.Str(e.SemanticName); w.U32(e.SemanticIndex); w.U32(e.Format); w.U32(e.InputSlot); w.U32(e.AlignedByteOffset);
        w.U32(e.InputSlotClass); w.U32(e.InstanceDataStepRate);
    }
}

inline void WriteGraphics(Writer& w, const D3D12_GRAPHICS_PIPELINE_STATE_DESC& d, const BlobSink& sink)
{
    w.U64(sink.rootSignature(d.pRootSignature));
    for (auto* s : { &d.VS, &d.PS, &d.DS, &d.HS, &d.GS }) WriteShader(w, *s, sink);
    WriteStreamOutput(w, d.StreamOutput);
    w.Pod(d.BlendState); w.U32(d.SampleMask); w.Pod(d.RasterizerState); w.Pod(d.DepthStencilState);
    WriteInputLayout(w, d.InputLayout);
    w.U32(d.IBStripCutValue); w.U32(d.PrimitiveTopologyType); w.U32(d.NumRenderTargets);
    for (auto f : d.RTVFormats) w.U32(f);
    w.U32(d.DSVFormat); w.Pod(d.SampleDesc); w.U32(d.NodeMask); w.U32(d.Flags);
}

inline void WriteCompute(Writer& w, const D3D12_COMPUTE_PIPELINE_STATE_DESC& d, const BlobSink& sink)
{
    w.U64(sink.rootSignature(d.pRootSignature));
    WriteShader(w, d.CS, sink);
    w.U32(d.NodeMask); w.U32(d.Flags);
}

/// Size and alignment of each subobject's inner struct (0 for types this version doesn't know).
inline std::pair<size_t, size_t> InnerLayout(D3D12_PIPELINE_STATE_SUBOBJECT_TYPE t)
{
#define WARM_L(T) { sizeof(T), alignof(T) }
    switch (t)
    {
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: return WARM_L(ID3D12RootSignature*);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS: return WARM_L(D3D12_SHADER_BYTECODE);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: return WARM_L(D3D12_STREAM_OUTPUT_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND: return WARM_L(D3D12_BLEND_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK: return WARM_L(UINT);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER: return WARM_L(D3D12_RASTERIZER_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL: return WARM_L(D3D12_DEPTH_STENCIL_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: return WARM_L(D3D12_INPUT_LAYOUT_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_IB_STRIP_CUT_VALUE: return WARM_L(D3D12_INDEX_BUFFER_STRIP_CUT_VALUE);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY: return WARM_L(D3D12_PRIMITIVE_TOPOLOGY_TYPE);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS: return WARM_L(D3D12_RT_FORMAT_ARRAY);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT: return WARM_L(DXGI_FORMAT);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC: return WARM_L(DXGI_SAMPLE_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_NODE_MASK: return WARM_L(UINT);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO: return WARM_L(D3D12_CACHED_PIPELINE_STATE);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_FLAGS: return WARM_L(D3D12_PIPELINE_STATE_FLAGS);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL1: return WARM_L(D3D12_DEPTH_STENCIL_DESC1);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING: return WARM_L(D3D12_VIEW_INSTANCING_DESC);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL2: return WARM_L(D3D12_DEPTH_STENCIL_DESC2);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER1: return WARM_L(D3D12_RASTERIZER_DESC1);
    case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER2: return WARM_L(D3D12_RASTERIZER_DESC2);
    default: return { 0, 0 };
    }
#undef WARM_L
}

constexpr size_t AlignUp(size_t v, size_t a) { return (v + a - 1) & ~(a - 1); }

/// Writes a pipeline state stream. False when it has a subobject type this version doesn't know (nothing usable then).
inline bool WriteStream(Writer& w, const D3D12_PIPELINE_STATE_STREAM_DESC& d, const BlobSink& sink)
{
    auto base = static_cast<const uint8_t*>(d.pPipelineStateSubobjectStream);
    std::vector<std::pair<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE, const uint8_t*>> parts;
    for (size_t off = 0; off + 4 <= d.SizeInBytes;)
    {
        auto type = *reinterpret_cast<const D3D12_PIPELINE_STATE_SUBOBJECT_TYPE*>(base + off);
        auto [size, align] = InnerLayout(type);
        if (size == 0) return false;
        size_t inner = AlignUp(off + sizeof(D3D12_PIPELINE_STATE_SUBOBJECT_TYPE), align);
        parts.emplace_back(type, base + inner);
        off = AlignUp(inner + size, sizeof(void*));
    }
    uint32_t count = 0;
    for (auto& [type, p] : parts) if (type != D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO) ++count;
    w.U32(count);
    for (auto& [type, p] : parts)
    {
        if (type == D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CACHED_PSO) continue;   // a driver blob for this exact PC: not replayed
        w.U32(type);
        switch (type)
        {
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: w.U64(sink.rootSignature(*reinterpret_cast<ID3D12RootSignature* const*>(p))); break;
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS:
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS:
            WriteShader(w, *reinterpret_cast<const D3D12_SHADER_BYTECODE*>(p), sink); break;
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: WriteStreamOutput(w, *reinterpret_cast<const D3D12_STREAM_OUTPUT_DESC*>(p)); break;
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: WriteInputLayout(w, *reinterpret_cast<const D3D12_INPUT_LAYOUT_DESC*>(p)); break;
        case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING:
        {
            auto& v = *reinterpret_cast<const D3D12_VIEW_INSTANCING_DESC*>(p);
            w.U32(v.pViewInstanceLocations ? v.ViewInstanceCount : 0);
            for (UINT i = 0; v.pViewInstanceLocations && i < v.ViewInstanceCount; ++i)
                { w.U32(v.pViewInstanceLocations[i].ViewportArrayIndex); w.U32(v.pViewInstanceLocations[i].RenderTargetArrayIndex); }
            w.U32(v.Flags);
            break;
        }
        default:   // plain values with no pointers: copied as they are
            w.Raw(p, InnerLayout(type).first);
        }
    }
    return true;
}

// ---------------------------------------------------------------- reading and rebuilding

class Reader
{
public:
    Reader(const uint8_t* p, size_t n) : p_(p), end_(p + n) {}
    bool ok = true;
    template <class T> T Pod() { T v{}; Take(&v, sizeof(T)); return v; }
    uint8_t U8() { return Pod<uint8_t>(); }
    uint32_t U32() { return Pod<uint32_t>(); }
    uint64_t U64() { return Pod<uint64_t>(); }
    void Take(void* dst, size_t n) { if (static_cast<size_t>(end_ - p_) < n) { ok = false; std::memset(dst, 0, n); return; } std::memcpy(dst, p_, n); p_ += n; }
    std::string Str() { uint32_t n = U32(); if (static_cast<size_t>(end_ - p_) < n) { ok = false; return {}; } std::string s(reinterpret_cast<const char*>(p_), n); p_ += n; return s; }
    bool AtEnd() const { return p_ == end_; }
private:
    const uint8_t* p_;
    const uint8_t* end_;
};

/// Where the replayer gets blobs and root signatures from. Returns nullptr when one can't be had.
struct BlobSource
{
    std::function<const std::vector<uint8_t>*(uint64_t)> blob;
    std::function<ID3D12RootSignature*(uint64_t)> rootSignature;
};

/// A rebuilt pipeline description and everything its pointers point at (kept alive while it's created).
struct Rebuilt
{
    RecordKind kind{};
    D3D12_GRAPHICS_PIPELINE_STATE_DESC graphics{};
    D3D12_COMPUTE_PIPELINE_STATE_DESC compute{};
    std::vector<uint8_t> stream;   // pointer-aligned subobject stream
    std::string problem;           // why it can't be rebuilt (empty when fine)
    // The recorded root signature it was rebuilt with. Kept because the object alone can't say: Direct3D 12 returns
    // the SAME root signature object for different files with the same content (a whole shader with an embedded
    // root signature, and that root signature on its own), so looking the file up from the object can pick the other.
    uint64_t rootSignatureRef = kNoRootSignature;
    std::deque<std::string> strings;
    std::deque<std::vector<D3D12_SO_DECLARATION_ENTRY>> soEntries;
    std::deque<std::vector<UINT>> strides;
    std::deque<std::vector<D3D12_INPUT_ELEMENT_DESC>> inputs;
    std::deque<std::vector<D3D12_VIEW_INSTANCE_LOCATION>> views;
};

inline D3D12_SHADER_BYTECODE ReadShader(Reader& r, const BlobSource& src, Rebuilt& out)
{
    uint64_t h = r.U64();
    if (h == 0) return {};
    auto* b = src.blob(h);
    if (!b) { out.problem = "a shader file is missing (" + Hex(h) + ")"; return {}; }
    return { b->data(), b->size() };
}

inline ID3D12RootSignature* ReadRootSignature(Reader& r, const BlobSource& src, Rebuilt& out)
{
    uint64_t h = r.U64();
    out.rootSignatureRef = h;
    if (h == kNoRootSignature) return nullptr;
    if (h == kUnknownRootSignature) { out.problem = "its root signature was created before the recorder started"; return nullptr; }
    auto* rs = src.rootSignature(h);
    if (!rs) out.problem = "its root signature couldn't be rebuilt (" + Hex(h) + ")";
    return rs;
}

inline D3D12_STREAM_OUTPUT_DESC ReadStreamOutput(Reader& r, Rebuilt& out)
{
    D3D12_STREAM_OUTPUT_DESC so{};
    uint32_t n = r.U32();
    auto& entries = out.soEntries.emplace_back(n);
    for (auto& e : entries)
    {
        e.Stream = r.U32();
        auto& s = out.strings.emplace_back(r.Str());
        e.SemanticName = s.empty() ? nullptr : s.c_str();
        e.SemanticIndex = r.U32(); e.StartComponent = r.U8(); e.ComponentCount = r.U8(); e.OutputSlot = r.U8();
    }
    uint32_t ns = r.U32();
    auto& st = out.strides.emplace_back(ns);
    for (auto& s : st) s = r.U32();
    so.pSODeclaration = n ? entries.data() : nullptr; so.NumEntries = n;
    so.pBufferStrides = ns ? st.data() : nullptr; so.NumStrides = ns;
    so.RasterizedStream = r.U32();
    return so;
}

inline D3D12_INPUT_LAYOUT_DESC ReadInputLayout(Reader& r, Rebuilt& out)
{
    uint32_t n = r.U32();
    auto& elems = out.inputs.emplace_back(n);
    for (auto& e : elems)
    {
        auto& s = out.strings.emplace_back(r.Str());
        e.SemanticName = s.c_str();
        e.SemanticIndex = r.U32(); e.Format = static_cast<DXGI_FORMAT>(r.U32()); e.InputSlot = r.U32(); e.AlignedByteOffset = r.U32();
        e.InputSlotClass = static_cast<D3D12_INPUT_CLASSIFICATION>(r.U32()); e.InstanceDataStepRate = r.U32();
    }
    return { n ? elems.data() : nullptr, n };
}

/// Appends one subobject to a stream the way the runtime expects it (pointer-aligned, inner at its own alignment).
template <class T> void AppendSubobject(std::vector<uint8_t>& s, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE type, const T& inner)
{
    size_t start = AlignUp(s.size(), sizeof(void*));
    size_t innerOff = AlignUp(start + sizeof(type), alignof(T));
    size_t end = AlignUp(innerOff + sizeof(T), sizeof(void*));
    s.resize(end, 0);
    std::memcpy(s.data() + start, &type, sizeof(type));
    std::memcpy(s.data() + innerOff, &inner, sizeof(T));
}

inline std::unique_ptr<Rebuilt> Rebuild(RecordKind kind, const uint8_t* payload, size_t size, const BlobSource& src)
{
    auto out = std::make_unique<Rebuilt>();
    out->kind = kind;
    Reader r(payload, size);
    if (kind == RecordKind::Graphics)
    {
        auto& d = out->graphics;
        d.pRootSignature = ReadRootSignature(r, src, *out);
        d.VS = ReadShader(r, src, *out); d.PS = ReadShader(r, src, *out); d.DS = ReadShader(r, src, *out);
        d.HS = ReadShader(r, src, *out); d.GS = ReadShader(r, src, *out);
        d.StreamOutput = ReadStreamOutput(r, *out);
        d.BlendState = r.Pod<D3D12_BLEND_DESC>(); d.SampleMask = r.U32();
        d.RasterizerState = r.Pod<D3D12_RASTERIZER_DESC>(); d.DepthStencilState = r.Pod<D3D12_DEPTH_STENCIL_DESC>();
        d.InputLayout = ReadInputLayout(r, *out);
        d.IBStripCutValue = static_cast<D3D12_INDEX_BUFFER_STRIP_CUT_VALUE>(r.U32());
        d.PrimitiveTopologyType = static_cast<D3D12_PRIMITIVE_TOPOLOGY_TYPE>(r.U32());
        d.NumRenderTargets = r.U32();
        for (auto& f : d.RTVFormats) f = static_cast<DXGI_FORMAT>(r.U32());
        d.DSVFormat = static_cast<DXGI_FORMAT>(r.U32()); d.SampleDesc = r.Pod<DXGI_SAMPLE_DESC>();
        d.NodeMask = r.U32(); d.Flags = static_cast<D3D12_PIPELINE_STATE_FLAGS>(r.U32());
    }
    else if (kind == RecordKind::Compute)
    {
        auto& d = out->compute;
        d.pRootSignature = ReadRootSignature(r, src, *out);
        d.CS = ReadShader(r, src, *out);
        d.NodeMask = r.U32(); d.Flags = static_cast<D3D12_PIPELINE_STATE_FLAGS>(r.U32());
    }
    else if (kind == RecordKind::Stream)
    {
        uint32_t count = r.U32();
        auto& s = out->stream;
        for (uint32_t i = 0; i < count && r.ok; ++i)
        {
            auto type = static_cast<D3D12_PIPELINE_STATE_SUBOBJECT_TYPE>(r.U32());
            switch (type)
            {
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE: AppendSubobject(s, type, ReadRootSignature(r, src, *out)); break;
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DS:
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_HS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_GS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_CS:
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_AS: case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS:
                AppendSubobject(s, type, ReadShader(r, src, *out)); break;
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_STREAM_OUTPUT: AppendSubobject(s, type, ReadStreamOutput(r, *out)); break;
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_INPUT_LAYOUT: AppendSubobject(s, type, ReadInputLayout(r, *out)); break;
            case D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_VIEW_INSTANCING:
            {
                uint32_t n = r.U32();
                auto& v = out->views.emplace_back(n);
                for (auto& l : v) { l.ViewportArrayIndex = r.U32(); l.RenderTargetArrayIndex = r.U32(); }
                D3D12_VIEW_INSTANCING_DESC vi{ n, n ? v.data() : nullptr, static_cast<D3D12_VIEW_INSTANCING_FLAGS>(r.U32()) };
                AppendSubobject(s, type, vi);
                break;
            }
            default:
            {
                auto [sz, al] = InnerLayout(type);
                if (sz == 0 || sz > 512) { out->problem = "unknown pipeline part"; return out; }
                alignas(16) uint8_t tmp[512];
                r.Take(tmp, sz);
                // The same bytes, placed with the inner type's alignment.
                size_t start = AlignUp(s.size(), sizeof(void*)), innerOff = AlignUp(start + 4, al), end = AlignUp(innerOff + sz, sizeof(void*));
                s.resize(end, 0);
                std::memcpy(s.data() + start, &type, 4);
                std::memcpy(s.data() + innerOff, tmp, sz);
            }
            }
        }
    }
    if (!r.ok && out->problem.empty()) out->problem = "the record is damaged";
    return out;
}

/// Creates the pipeline. Returns the result and how long the driver took (milliseconds).
inline HRESULT CreateRebuilt(ID3D12Device2* device, Rebuilt& p, double& ms)
{
    Microsoft::WRL::ComPtr<ID3D12PipelineState> pso;
    double t0 = NowMs();
    HRESULT hr = E_INVALIDARG;
    if (p.kind == RecordKind::Graphics) hr = device->CreateGraphicsPipelineState(&p.graphics, IID_PPV_ARGS(&pso));
    else if (p.kind == RecordKind::Compute) hr = device->CreateComputePipelineState(&p.compute, IID_PPV_ARGS(&pso));
    else if (p.kind == RecordKind::Stream)
    {
        D3D12_PIPELINE_STATE_STREAM_DESC d{ p.stream.size(), p.stream.data() };
        hr = device->CreatePipelineState(&d, IID_PPV_ARGS(&pso));
    }
    ms = NowMs() - t0;
    return hr;
}

constexpr char kRecordMagic[8] = { 'U', 'S', 'W', 'R', 'E', 'C', '1', '\0' };

/// One record from pipelines.bin.
struct StoredRecord { RecordKind kind; uint64_t id; std::vector<uint8_t> payload; };

/// Reads every record of a pipelines.bin (stops quietly at a cut-off end, e.g. when the game was killed).
inline std::vector<StoredRecord> ReadRecords(const fs::path& file)
{
    std::vector<StoredRecord> out;
    std::ifstream in(file, std::ios::binary);
    char magic[8]{};
    if (!in.read(magic, 8) || std::memcmp(magic, kRecordMagic, 8) != 0) return out;
    while (true)
    {
        uint32_t kind = 0, size = 0;
        uint64_t id = 0;
        if (!in.read(reinterpret_cast<char*>(&kind), 4) || !in.read(reinterpret_cast<char*>(&id), 8) || !in.read(reinterpret_cast<char*>(&size), 4)) break;
        if (size > 64 * 1024 * 1024) break;
        StoredRecord r{ static_cast<RecordKind>(kind), id, std::vector<uint8_t>(size) };
        if (size && !in.read(reinterpret_cast<char*>(r.payload.data()), size)) break;
        out.push_back(std::move(r));
    }
    return out;
}
} // namespace warm
