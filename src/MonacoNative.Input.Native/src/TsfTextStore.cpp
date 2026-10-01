#ifdef _WIN32

#include "MonacoNative/Input/Native/TsfTextStore.h"
#include "MonacoNative/Input/Native/MemoryAcpTextStoreBackend.h"

#include <algorithm>
#include <ocidl.h>
#include <cstring>
#include <new>
#include <string>
#include <utility>

// Windows SDKs do not consistently surface the COM connection-point HRESULT
// names through <ocidl.h> alone. Microsoft documents the values as
// CONNECT_E_NOCONNECTION = 0x80040200 and CONNECT_E_ADVISELIMIT = 0x80040201.
// Keep the documented numeric fallbacks locally instead of including <olectl.h>;
// that header can introduce duplicate OLE helper definitions on current MSVC/SDK pairs.
#ifndef CONNECT_E_NOCONNECTION
#define CONNECT_E_NOCONNECTION _HRESULT_TYPEDEF_(0x80040200L)
#endif
#ifndef CONNECT_E_ADVISELIMIT
#define CONNECT_E_ADVISELIMIT _HRESULT_TYPEDEF_(0x80040201L)
#endif

namespace MonacoNative::Input::Native
{
    namespace
    {
        constexpr TsViewCookie kActiveView = 1;

        std::u16string ToUtf16(const WCHAR* text, ULONG length)
        {
            static_assert(sizeof(WCHAR) == sizeof(char16_t));
            if (length == 0)
            {
                return {};
            }
            return std::u16string(
                reinterpret_cast<const char16_t*>(text),
                reinterpret_cast<const char16_t*>(text) + length);
        }
    }

    TsfTextStore::TsfTextStore(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        std::shared_ptr<IAcpTextStoreGeometry> geometry) noexcept
        : backend_(std::move(backend)), geometry_(std::move(geometry))
    {
        if (backend_)
        {
            backendChangeToken_ = backend_->AddExternalChangeHandler([this](const AcpExternalChangeEvent& event)
            {
                OnExternalBackendChange(event);
            });
        }
    }

    TsfTextStore::~TsfTextStore()
    {
        if (backend_ && backendChangeToken_ != 0)
        {
            backend_->RemoveExternalChangeHandler(backendChangeToken_);
        }
        if (sink_)
        {
            sink_->Release();
        }
        if (sinkIdentity_)
        {
            sinkIdentity_->Release();
        }
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::QueryInterface(REFIID riid, void** object) noexcept
    {
        if (!object)
        {
            return E_POINTER;
        }
        *object = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITextStoreACP2))
        {
            *object = static_cast<ITextStoreACP2*>(this);
            AddRef();
            return S_OK;
        }
        if (IsEqualIID(riid, IID_ITfContextOwnerCompositionSink))
        {
            *object = static_cast<ITfContextOwnerCompositionSink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE TsfTextStore::AddRef() noexcept
    {
        return ++refCount_;
    }

    ULONG STDMETHODCALLTYPE TsfTextStore::Release() noexcept
    {
        const auto remaining = --refCount_;
        if (remaining == 0)
        {
            delete this;
        }
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::AdviseSink(REFIID riid, IUnknown* unknown, DWORD mask) noexcept
    {
        if (!unknown || !IsEqualIID(riid, IID_ITextStoreACPSink))
        {
            return E_INVALIDARG;
        }

        IUnknown* identity = nullptr;
        auto hr = unknown->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&identity));
        if (FAILED(hr))
        {
            return hr;
        }

        if (sinkIdentity_)
        {
            if (identity == sinkIdentity_)
            {
                sinkMask_ = mask;
                identity->Release();
                return S_OK;
            }
            identity->Release();
            return CONNECT_E_ADVISELIMIT;
        }

        ITextStoreACPSink* sink = nullptr;
        hr = unknown->QueryInterface(IID_ITextStoreACPSink, reinterpret_cast<void**>(&sink));
        if (FAILED(hr))
        {
            identity->Release();
            return hr;
        }

        sinkIdentity_ = identity;
        sink_ = sink;
        sinkMask_ = mask;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::UnadviseSink(IUnknown* unknown) noexcept
    {
        if (!unknown || !sinkIdentity_)
        {
            return CONNECT_E_NOCONNECTION;
        }
        if (!SameComIdentity(unknown, sinkIdentity_))
        {
            return CONNECT_E_NOCONNECTION;
        }

        sink_->Release();
        sink_ = nullptr;
        sinkIdentity_->Release();
        sinkIdentity_ = nullptr;
        sinkMask_ = 0;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::RequestLock(DWORD lockFlags, HRESULT* sessionResult) noexcept
    {
        if (!sessionResult)
        {
            return E_INVALIDARG;
        }
        *sessionResult = E_FAIL;
        if (!sink_)
        {
            return E_UNEXPECTED;
        }

        if (locked_)
        {
            if ((lockFlags & TS_LF_SYNC) != 0)
            {
                *sessionResult = TS_E_SYNCHRONOUS;
                return S_OK;
            }

            if (HasReadLock() && !HasReadWriteLock() && (lockFlags & TS_LF_READWRITE) == TS_LF_READWRITE)
            {
                pendingReadWriteUpgrade_ = true;
                *sessionResult = TS_S_ASYNC;
                return S_OK;
            }
            return E_FAIL;
        }

        return GrantLock(lockFlags, sessionResult);
    }

    HRESULT TsfTextStore::GrantLock(DWORD lockFlags, HRESULT* sessionResult) noexcept
    {
        locked_ = true;
        lockType_ = lockFlags & TS_LF_READWRITE;
        *sessionResult = sink_->OnLockGranted(lockFlags);
        locked_ = false;
        lockType_ = 0;

        if (pendingReadWriteUpgrade_)
        {
            pendingReadWriteUpgrade_ = false;
            HRESULT ignored{};
            locked_ = true;
            lockType_ = TS_LF_READWRITE;
            ignored = sink_->OnLockGranted(TS_LF_READWRITE);
            (void)ignored;
            locked_ = false;
            lockType_ = 0;
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetStatus(TS_STATUS* status) noexcept
    {
        if (!status)
        {
            return E_INVALIDARG;
        }
        status->dwDynamicFlags = backend_->IsReadOnly() ? TS_SD_READONLY : 0;
        status->dwStaticFlags = TS_SS_NOHIDDENTEXT;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::QueryInsert(
        LONG testStart,
        LONG testEnd,
        ULONG,
        LONG* resultStart,
        LONG* resultEnd) noexcept
    {
        if (!resultStart || !resultEnd || !IsValidRange(testStart, testEnd))
        {
            return E_INVALIDARG;
        }
        *resultStart = testStart;
        *resultEnd = testEnd;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetSelection(
        ULONG index,
        ULONG count,
        TS_SELECTION_ACP* selection,
        ULONG* fetched) noexcept
    {
        if (!selection || !fetched || count == 0)
        {
            return E_INVALIDARG;
        }
        *fetched = 0;
        if (!HasReadLock())
        {
            return TS_E_NOLOCK;
        }
        if (index != TF_DEFAULT_SELECTION && index != 0)
        {
            return E_INVALIDARG;
        }

        const auto current = backend_->GetSelection();
        selection[0].acpStart = current.start;
        selection[0].acpEnd = current.end;
        selection[0].style.ase = current.interimCharacter
            ? TS_AE_NONE
            : (current.activeEndAtStart ? TS_AE_START : TS_AE_END);
        selection[0].style.fInterimChar = current.interimCharacter ? TRUE : FALSE;
        *fetched = 1;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::SetSelection(ULONG count, const TS_SELECTION_ACP* selection) noexcept
    {
        if (!selection || count != 1)
        {
            return E_INVALIDARG;
        }
        if (!HasReadWriteLock())
        {
            return TS_E_NOLOCK;
        }
        if (!IsValidRange(selection[0].acpStart, selection[0].acpEnd))
        {
            return TS_E_INVALIDPOS;
        }

        const AcpSelection value{
            selection[0].acpStart,
            selection[0].acpEnd,
            selection[0].style.ase == TS_AE_START,
            selection[0].style.fInterimChar != FALSE,
        };
        return backend_->SetSelection(value) ? S_OK : TS_E_INVALIDPOS;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetText(
        LONG start,
        LONG end,
        WCHAR* plain,
        ULONG plainCapacity,
        ULONG* plainLength,
        TS_RUNINFO* runInfo,
        ULONG runInfoCapacity,
        ULONG* runInfoLength,
        LONG* nextAcp) noexcept
    {
        if (!HasReadLock())
        {
            return TS_E_NOLOCK;
        }
        if ((plainCapacity != 0 && !plain) || (runInfoCapacity != 0 && !runInfo))
        {
            return E_INVALIDARG;
        }
        if (!IsValidPosition(start))
        {
            return TS_E_INVALIDPOS;
        }

        const auto documentEnd = backend_->Length();
        const LONG normalizedEnd = end == -1 ? documentEnd : end;
        if (!IsValidRange(start, normalizedEnd))
        {
            return TS_E_INVALIDPOS;
        }

        if (plainLength)
        {
            *plainLength = 0;
        }
        if (runInfoLength)
        {
            *runInfoLength = 0;
        }
        if (nextAcp)
        {
            *nextAcp = start;
        }

        const auto requested = static_cast<ULONG>(normalizedEnd - start);
        const bool wantsPlain = plainCapacity > 0;
        const bool wantsRuns = runInfoCapacity > 0;
        const auto transferred = wantsPlain ? std::min(requested, plainCapacity) : (wantsRuns ? requested : 0u);
        const auto copied = wantsPlain ? transferred : 0u;
        if (copied > 0)
        {
            try
            {
                const auto value = backend_->GetText(start, start + static_cast<LONG>(copied));
                std::memcpy(plain, value.data(), static_cast<std::size_t>(copied) * sizeof(char16_t));
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        if (plainLength)
        {
            *plainLength = copied;
        }
        if (runInfoCapacity > 0 && runInfoLength)
        {
            runInfo[0].type = TS_RT_PLAIN;
            runInfo[0].uCount = transferred;
            *runInfoLength = transferred > 0 ? 1u : 0u;
        }
        if (nextAcp)
        {
            *nextAcp = start + static_cast<LONG>(transferred);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::SetText(
        DWORD,
        LONG start,
        LONG end,
        const WCHAR* text,
        ULONG textLength,
        TS_TEXTCHANGE* change) noexcept
    {
        if (!HasReadWriteLock())
        {
            return TS_E_NOLOCK;
        }
        if ((textLength != 0 && !text) || !change)
        {
            return E_INVALIDARG;
        }
        if (!IsValidRange(start, end))
        {
            return TS_E_INVALIDPOS;
        }

        TS_SELECTION_ACP selection{};
        selection.acpStart = start;
        selection.acpEnd = end;
        selection.style.ase = TS_AE_END;
        selection.style.fInterimChar = FALSE;
        auto hr = SetSelection(1, &selection);
        if (FAILED(hr))
        {
            return hr;
        }
        return InsertTextAtSelection(TF_IAS_NOQUERY, text, textLength, nullptr, nullptr, change);
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetFormattedText(LONG, LONG, IDataObject** dataObject) noexcept
    {
        if (dataObject)
        {
            *dataObject = nullptr;
        }
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetEmbedded(LONG, REFGUID, REFIID, IUnknown** unknown) noexcept
    {
        if (unknown)
        {
            *unknown = nullptr;
        }
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::QueryInsertEmbedded(const GUID*, const FORMATETC*, BOOL* insertable) noexcept
    {
        if (!insertable)
        {
            return E_INVALIDARG;
        }
        *insertable = FALSE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::InsertEmbedded(DWORD, LONG, LONG, IDataObject*, TS_TEXTCHANGE*) noexcept
    {
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::InsertTextAtSelection(
        DWORD flags,
        const WCHAR* text,
        ULONG textLength,
        LONG* start,
        LONG* end,
        TS_TEXTCHANGE* change) noexcept
    {
        const bool queryOnly = (flags & TF_IAS_QUERYONLY) != 0;
        const bool noQuery = (flags & TF_IAS_NOQUERY) != 0;
        if (queryOnly && noQuery)
        {
            return E_INVALIDARG;
        }
        if (textLength != 0 && !text)
        {
            return E_INVALIDARG;
        }
        if (queryOnly)
        {
            if (!HasReadLock())
            {
                return TS_E_NOLOCK;
            }
        }
        else if (!HasReadWriteLock())
        {
            return TS_E_NOLOCK;
        }

        const auto selection = backend_->GetSelection();
        if (!IsValidRange(selection.start, selection.end))
        {
            return TS_E_INVALIDPOS;
        }
        if (textLength > static_cast<ULONG>(LONG_MAX) ||
            static_cast<unsigned long long>(selection.start) + textLength > static_cast<unsigned long long>(LONG_MAX))
        {
            return E_INVALIDARG;
        }
        const LONG newEnd = selection.start + static_cast<LONG>(textLength);

        if (!noQuery)
        {
            if (!start || !end)
            {
                return E_INVALIDARG;
            }
            *start = selection.start;
            *end = newEnd;
        }

        if (queryOnly)
        {
            if (change)
            {
                change->acpStart = selection.start;
                change->acpOldEnd = selection.end;
                change->acpNewEnd = newEnd;
            }
            return S_OK;
        }

        if (!change)
        {
            return E_INVALIDARG;
        }

        AcpTextChange backendChange{};
        try
        {
            if (!backend_->Replace(selection.start, selection.end, ToUtf16(text, textLength), backendChange))
            {
                return E_FAIL;
            }
        }
        catch (...)
        {
            return E_FAIL;
        }

        change->acpStart = backendChange.start;
        change->acpOldEnd = backendChange.oldEnd;
        change->acpNewEnd = backendChange.newEnd;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::InsertEmbeddedAtSelection(
        DWORD,
        IDataObject*,
        LONG*,
        LONG*,
        TS_TEXTCHANGE*) noexcept
    {
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::RequestSupportedAttrs(DWORD, ULONG, const TS_ATTRID*) noexcept
    {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::RequestAttrsAtPosition(
        LONG position,
        ULONG,
        const TS_ATTRID*,
        DWORD) noexcept
    {
        return IsValidPosition(position) ? S_OK : TS_E_INVALIDPOS;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::RequestAttrsTransitioningAtPosition(
        LONG position,
        ULONG,
        const TS_ATTRID*,
        DWORD) noexcept
    {
        return IsValidPosition(position) ? S_OK : TS_E_INVALIDPOS;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::FindNextAttrTransition(
        LONG start,
        LONG halt,
        ULONG,
        const TS_ATTRID*,
        DWORD,
        LONG* next,
        BOOL* found,
        LONG* foundOffset) noexcept
    {
        if (!next || !found || !foundOffset || !IsValidPosition(start) || !IsValidPosition(halt))
        {
            return E_INVALIDARG;
        }
        *next = halt;
        *found = FALSE;
        *foundOffset = 0;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::RetrieveRequestedAttrs(ULONG, TS_ATTRVAL*, ULONG* fetched) noexcept
    {
        if (!fetched)
        {
            return E_INVALIDARG;
        }
        *fetched = 0;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetEndACP(LONG* end) noexcept
    {
        if (!end)
        {
            return E_INVALIDARG;
        }
        if (!HasReadLock())
        {
            return TS_E_NOLOCK;
        }
        *end = backend_->Length();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetActiveView(TsViewCookie* viewCookie) noexcept
    {
        if (!viewCookie)
        {
            return E_INVALIDARG;
        }
        *viewCookie = kActiveView;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetACPFromPoint(
        TsViewCookie viewCookie,
        const POINT* screenPoint,
        DWORD flags,
        LONG* acp) noexcept
    {
        if (viewCookie != kActiveView || !screenPoint || !acp)
        {
            return E_INVALIDARG;
        }
        if (!geometry_ || !geometry_->HasLayout())
        {
            return TS_E_NOLAYOUT;
        }
        std::int32_t mapped{};
        if (!geometry_->GetAcpFromScreenPoint(
                AcpScreenPoint{screenPoint->x, screenPoint->y},
                static_cast<std::uint32_t>(flags),
                mapped))
        {
            return TS_E_INVALIDPOINT;
        }
        if (!IsValidPosition(mapped))
        {
            return TS_E_INVALIDPOS;
        }
        *acp = mapped;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetTextExt(
        TsViewCookie viewCookie,
        LONG start,
        LONG end,
        RECT* rect,
        BOOL* clipped) noexcept
    {
        if (viewCookie != kActiveView || !rect || !clipped)
        {
            return E_INVALIDARG;
        }
        if (!HasReadLock())
        {
            return TS_E_NOLOCK;
        }
        // Although the API documentation mentions TS_E_INVALIDARG for a collapsed
        // range, current Windows SDK headers do not define that HRESULT.  More
        // importantly, real TIPs query caret geometry with start == end.  Treat a
        // collapsed range as a caret rectangle and let the geometry provider answer it.
        if (!IsValidRange(start, end))
        {
            return TS_E_INVALIDPOS;
        }
        if (!geometry_ || !geometry_->HasLayout())
        {
            return TS_E_NOLAYOUT;
        }
        AcpScreenRect value{};
        bool wasClipped{};
        if (!geometry_->GetTextScreenRect(start, end, value, wasClipped))
        {
            return TS_E_NOLAYOUT;
        }
        *rect = RECT{value.left, value.top, value.right, value.bottom};
        *clipped = wasClipped ? TRUE : FALSE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::GetScreenExt(TsViewCookie viewCookie, RECT* rect) noexcept
    {
        if (viewCookie != kActiveView || !rect)
        {
            return E_INVALIDARG;
        }
        if (!geometry_)
        {
            *rect = RECT{};
            return S_OK;
        }
        AcpScreenRect value{};
        if (!geometry_->GetScreenRect(value))
        {
            *rect = RECT{};
            return S_OK;
        }
        *rect = RECT{value.left, value.top, value.right, value.bottom};
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::OnStartComposition(ITfCompositionView* composition, BOOL* ok) noexcept
    {
        if (!ok)
        {
            return E_INVALIDARG;
        }
        *ok = TRUE;
        LONG start{};
        LONG end{};
        if (!TryGetCompositionExtent(composition, start, end))
        {
            const auto selection = backend_ ? backend_->GetSelection() : AcpSelection{};
            start = selection.start;
            end = selection.end;
        }
        compositionActive_ = true;
        compositionRange_ = AcpTextChange{start, end, end};
        EmitComposition(TsfCompositionPhase::Started, start, end);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::OnUpdateComposition(ITfCompositionView* composition, ITfRange* rangeNew) noexcept
    {
        LONG start{};
        LONG end{};
        const bool hasNewRange = TryGetRangeExtent(rangeNew, start, end);
        if (!hasNewRange && !TryGetCompositionExtent(composition, start, end))
        {
            start = compositionRange_.start;
            end = compositionRange_.newEnd;
        }
        compositionActive_ = true;
        compositionRange_ = AcpTextChange{start, end, end};
        EmitComposition(TsfCompositionPhase::Updated, start, end);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE TsfTextStore::OnEndComposition(ITfCompositionView*) noexcept
    {
        const auto start = compositionRange_.start;
        const auto end = compositionRange_.newEnd;
        compositionActive_ = false;
        EmitComposition(TsfCompositionPhase::Ended, start, end);
        compositionRange_ = {};
        return S_OK;
    }

    void TsfTextStore::SetCompositionHandler(CompositionHandler handler)
    {
        compositionHandler_ = std::move(handler);
    }

    bool TsfTextStore::HasReadLock() const noexcept
    {
        return locked_ && (lockType_ & TS_LF_READ) == TS_LF_READ;
    }

    bool TsfTextStore::HasReadWriteLock() const noexcept
    {
        return locked_ && (lockType_ & TS_LF_READWRITE) == TS_LF_READWRITE;
    }

    bool TsfTextStore::IsValidPosition(LONG position) const noexcept
    {
        return position >= 0 && position <= backend_->Length();
    }

    bool TsfTextStore::IsValidRange(LONG start, LONG end) const noexcept
    {
        return start >= 0 && end >= start && end <= backend_->Length();
    }

    bool TsfTextStore::TryGetRangeExtent(ITfRange* range, LONG& start, LONG& end) noexcept
    {
        if (!range)
        {
            return false;
        }
        ITfRangeACP* acpRange = nullptr;
        const auto hr = range->QueryInterface(IID_ITfRangeACP, reinterpret_cast<void**>(&acpRange));
        if (FAILED(hr) || !acpRange)
        {
            return false;
        }
        LONG length{};
        const auto extentHr = acpRange->GetExtent(&start, &length);
        acpRange->Release();
        if (FAILED(extentHr) || length < 0 || start < 0 || start > LONG_MAX - length)
        {
            return false;
        }
        end = start + length;
        return true;
    }

    bool TsfTextStore::TryGetCompositionExtent(ITfCompositionView* composition, LONG& start, LONG& end) noexcept
    {
        if (!composition)
        {
            return false;
        }
        ITfRange* range = nullptr;
        const auto hr = composition->GetRange(&range);
        if (FAILED(hr) || !range)
        {
            return false;
        }
        const auto result = TryGetRangeExtent(range, start, end);
        range->Release();
        return result;
    }

    void TsfTextStore::EmitComposition(TsfCompositionPhase phase, LONG start, LONG end) noexcept
    {
        if (!compositionHandler_)
        {
            return;
        }
        try
        {
            compositionHandler_(TsfCompositionEvent{
                phase,
                static_cast<std::int32_t>(start),
                static_cast<std::int32_t>(end),
            });
        }
        catch (...)
        {
            // COM callbacks must not allow host exceptions to cross the ABI.
        }
    }

    void TsfTextStore::OnExternalBackendChange(const AcpExternalChangeEvent& event) noexcept
    {
        if (!sink_)
        {
            return;
        }

        if ((sinkMask_ & TS_AS_TEXT_CHANGE) != 0)
        {
            // Model batches use pre-edit offsets and TextModel applies them from
            // the end backwards. Notify in the same direction so earlier edits
            // do not invalidate the ACPs of later disjoint edits.
            for (auto iterator = event.textChanges.rbegin(); iterator != event.textChanges.rend(); ++iterator)
            {
                TS_TEXTCHANGE change{};
                change.acpStart = iterator->start;
                change.acpOldEnd = iterator->oldEnd;
                change.acpNewEnd = iterator->newEnd;
                (void)sink_->OnTextChange(0, &change);
            }
        }
        if (event.selectionChanged && (sinkMask_ & TS_AS_SEL_CHANGE) != 0)
        {
            (void)sink_->OnSelectionChange();
        }
    }

    bool TsfTextStore::SameComIdentity(IUnknown* left, IUnknown* right) noexcept
    {
        if (!left || !right)
        {
            return false;
        }
        IUnknown* leftIdentity = nullptr;
        IUnknown* rightIdentity = nullptr;
        const auto leftHr = left->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&leftIdentity));
        const auto rightHr = right->QueryInterface(IID_IUnknown, reinterpret_cast<void**>(&rightIdentity));
        const bool same = SUCCEEDED(leftHr) && SUCCEEDED(rightHr) && leftIdentity == rightIdentity;
        if (leftIdentity)
        {
            leftIdentity->Release();
        }
        if (rightIdentity)
        {
            rightIdentity->Release();
        }
        return same;
    }

    HRESULT CreateTsfTextStore(std::shared_ptr<IAcpTextStoreBackend> backend, ITextStoreACP2** store) noexcept
    {
        return CreateTsfTextStore(std::move(backend), {}, store);
    }

    HRESULT CreateTsfTextStore(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        std::shared_ptr<IAcpTextStoreGeometry> geometry,
        ITextStoreACP2** store) noexcept
    {
        if (!store)
        {
            return E_POINTER;
        }
        *store = nullptr;
        if (!backend)
        {
            return E_INVALIDARG;
        }

        auto* instance = new (std::nothrow) TsfTextStore(std::move(backend), std::move(geometry));
        if (!instance)
        {
            return E_OUTOFMEMORY;
        }
        *store = instance;
        return S_OK;
    }

    HRESULT CreateMemoryTsfTextStore(std::u16string initialText, ITextStoreACP2** store) noexcept
    {
        try
        {
            return CreateTsfTextStore(
                std::make_shared<MemoryAcpTextStoreBackend>(std::move(initialText)),
                store);
        }
        catch (...)
        {
            if (store)
            {
                *store = nullptr;
            }
            return E_FAIL;
        }
    }
}

#endif
