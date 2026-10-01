#pragma once

#ifdef _WIN32

#include "MonacoNative/Input/Native/AcpTextStoreBackend.h"
#include "MonacoNative/Input/Native/AcpTextStoreGeometry.h"

#include <Windows.h>
#include <msctf.h>
#include <textstor.h>

#include <atomic>
#include <functional>
#include <memory>

namespace MonacoNative::Input::Native
{
    enum class TsfCompositionPhase
    {
        Started,
        Updated,
        Ended,
    };

    struct TsfCompositionEvent final
    {
        TsfCompositionPhase phase{TsfCompositionPhase::Started};
        std::int32_t start{};
        std::int32_t end{};
    };

    class TsfTextStore final : public ITextStoreACP2, public ITfContextOwnerCompositionSink
    {
    public:
        using CompositionHandler = std::function<void(const TsfCompositionEvent&)>;

        explicit TsfTextStore(
            std::shared_ptr<IAcpTextStoreBackend> backend,
            std::shared_ptr<IAcpTextStoreGeometry> geometry = {}) noexcept;

        TsfTextStore(const TsfTextStore&) = delete;
        TsfTextStore& operator=(const TsfTextStore&) = delete;

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) noexcept override;
        ULONG STDMETHODCALLTYPE AddRef() noexcept override;
        ULONG STDMETHODCALLTYPE Release() noexcept override;

        HRESULT STDMETHODCALLTYPE AdviseSink(REFIID riid, IUnknown* unknown, DWORD mask) noexcept override;
        HRESULT STDMETHODCALLTYPE UnadviseSink(IUnknown* unknown) noexcept override;
        HRESULT STDMETHODCALLTYPE RequestLock(DWORD lockFlags, HRESULT* sessionResult) noexcept override;
        HRESULT STDMETHODCALLTYPE GetStatus(TS_STATUS* status) noexcept override;
        HRESULT STDMETHODCALLTYPE QueryInsert(
            LONG testStart,
            LONG testEnd,
            ULONG textLength,
            LONG* resultStart,
            LONG* resultEnd) noexcept override;
        HRESULT STDMETHODCALLTYPE GetSelection(
            ULONG index,
            ULONG count,
            TS_SELECTION_ACP* selection,
            ULONG* fetched) noexcept override;
        HRESULT STDMETHODCALLTYPE SetSelection(ULONG count, const TS_SELECTION_ACP* selection) noexcept override;
        HRESULT STDMETHODCALLTYPE GetText(
            LONG start,
            LONG end,
            WCHAR* plain,
            ULONG plainCapacity,
            ULONG* plainLength,
            TS_RUNINFO* runInfo,
            ULONG runInfoCapacity,
            ULONG* runInfoLength,
            LONG* nextAcp) noexcept override;
        HRESULT STDMETHODCALLTYPE SetText(
            DWORD flags,
            LONG start,
            LONG end,
            const WCHAR* text,
            ULONG textLength,
            TS_TEXTCHANGE* change) noexcept override;
        HRESULT STDMETHODCALLTYPE GetFormattedText(LONG start, LONG end, IDataObject** dataObject) noexcept override;
        HRESULT STDMETHODCALLTYPE GetEmbedded(
            LONG position,
            REFGUID service,
            REFIID riid,
            IUnknown** unknown) noexcept override;
        HRESULT STDMETHODCALLTYPE QueryInsertEmbedded(
            const GUID* service,
            const FORMATETC* format,
            BOOL* insertable) noexcept override;
        HRESULT STDMETHODCALLTYPE InsertEmbedded(
            DWORD flags,
            LONG start,
            LONG end,
            IDataObject* dataObject,
            TS_TEXTCHANGE* change) noexcept override;
        HRESULT STDMETHODCALLTYPE InsertTextAtSelection(
            DWORD flags,
            const WCHAR* text,
            ULONG textLength,
            LONG* start,
            LONG* end,
            TS_TEXTCHANGE* change) noexcept override;
        HRESULT STDMETHODCALLTYPE InsertEmbeddedAtSelection(
            DWORD flags,
            IDataObject* dataObject,
            LONG* start,
            LONG* end,
            TS_TEXTCHANGE* change) noexcept override;
        HRESULT STDMETHODCALLTYPE RequestSupportedAttrs(
            DWORD flags,
            ULONG filterCount,
            const TS_ATTRID* filterAttrs) noexcept override;
        HRESULT STDMETHODCALLTYPE RequestAttrsAtPosition(
            LONG position,
            ULONG filterCount,
            const TS_ATTRID* filterAttrs,
            DWORD flags) noexcept override;
        HRESULT STDMETHODCALLTYPE RequestAttrsTransitioningAtPosition(
            LONG position,
            ULONG filterCount,
            const TS_ATTRID* filterAttrs,
            DWORD flags) noexcept override;
        HRESULT STDMETHODCALLTYPE FindNextAttrTransition(
            LONG start,
            LONG halt,
            ULONG filterCount,
            const TS_ATTRID* filterAttrs,
            DWORD flags,
            LONG* next,
            BOOL* found,
            LONG* foundOffset) noexcept override;
        HRESULT STDMETHODCALLTYPE RetrieveRequestedAttrs(
            ULONG count,
            TS_ATTRVAL* values,
            ULONG* fetched) noexcept override;
        HRESULT STDMETHODCALLTYPE GetEndACP(LONG* end) noexcept override;
        HRESULT STDMETHODCALLTYPE GetActiveView(TsViewCookie* viewCookie) noexcept override;
        HRESULT STDMETHODCALLTYPE GetACPFromPoint(
            TsViewCookie viewCookie,
            const POINT* screenPoint,
            DWORD flags,
            LONG* acp) noexcept override;
        HRESULT STDMETHODCALLTYPE GetTextExt(
            TsViewCookie viewCookie,
            LONG start,
            LONG end,
            RECT* rect,
            BOOL* clipped) noexcept override;
        HRESULT STDMETHODCALLTYPE GetScreenExt(TsViewCookie viewCookie, RECT* rect) noexcept override;

        HRESULT STDMETHODCALLTYPE OnStartComposition(ITfCompositionView* composition, BOOL* ok) noexcept override;
        HRESULT STDMETHODCALLTYPE OnUpdateComposition(ITfCompositionView* composition, ITfRange* rangeNew) noexcept override;
        HRESULT STDMETHODCALLTYPE OnEndComposition(ITfCompositionView* composition) noexcept override;

        void SetCompositionHandler(CompositionHandler handler);
        [[nodiscard]] bool IsCompositionActive() const noexcept { return compositionActive_; }
        [[nodiscard]] AcpTextChange CompositionRange() const noexcept { return compositionRange_; }
        [[nodiscard]] const std::shared_ptr<IAcpTextStoreBackend>& Backend() const noexcept { return backend_; }
        [[nodiscard]] const std::shared_ptr<IAcpTextStoreGeometry>& Geometry() const noexcept { return geometry_; }

    private:
        ~TsfTextStore();

        [[nodiscard]] bool HasReadLock() const noexcept;
        [[nodiscard]] bool HasReadWriteLock() const noexcept;
        [[nodiscard]] bool IsValidPosition(LONG position) const noexcept;
        [[nodiscard]] bool IsValidRange(LONG start, LONG end) const noexcept;
        HRESULT GrantLock(DWORD lockFlags, HRESULT* sessionResult) noexcept;
        static bool SameComIdentity(IUnknown* left, IUnknown* right) noexcept;
        static bool TryGetRangeExtent(ITfRange* range, LONG& start, LONG& end) noexcept;
        static bool TryGetCompositionExtent(ITfCompositionView* composition, LONG& start, LONG& end) noexcept;
        void EmitComposition(TsfCompositionPhase phase, LONG start, LONG end) noexcept;
        void OnExternalBackendChange(const AcpExternalChangeEvent& event) noexcept;

        std::atomic<ULONG> refCount_{1};
        std::shared_ptr<IAcpTextStoreBackend> backend_;
        std::shared_ptr<IAcpTextStoreGeometry> geometry_;
        CompositionHandler compositionHandler_{};
        AcpTextChange compositionRange_{};
        bool compositionActive_{};
        IUnknown* sinkIdentity_{};
        ITextStoreACPSink* sink_{};
        DWORD sinkMask_{};
        IAcpTextStoreBackend::ExternalChangeToken backendChangeToken_{};
        bool locked_{};
        DWORD lockType_{};
        bool pendingReadWriteUpgrade_{};
    };

    HRESULT CreateTsfTextStore(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        ITextStoreACP2** store) noexcept;

    HRESULT CreateTsfTextStore(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        std::shared_ptr<IAcpTextStoreGeometry> geometry,
        ITextStoreACP2** store) noexcept;

    HRESULT CreateMemoryTsfTextStore(
        std::u16string initialText,
        ITextStoreACP2** store) noexcept;
}

#endif
