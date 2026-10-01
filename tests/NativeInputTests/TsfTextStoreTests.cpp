#ifdef _WIN32

#include "MonacoNative/Input/Native/TsfDocumentSession.h"
#include "MonacoNative/Input/Native/TsfTextStore.h"
#include "MonacoNative/Input/Native/MemoryAcpTextStoreBackend.h"
#include "MonacoNative/Input/Native/TextModelAcpTextStoreBackend.h"
#include "MonacoNative/Core/CursorModel.h"
#include "MonacoNative/Core/TextModel.h"

#include <Windows.h>
#include <textstor.h>

#include <atomic>
#include <functional>
#include <iostream>
#include <memory>
#include <string>

using MonacoNative::Input::Native::AcpScreenPoint;
using MonacoNative::Input::Native::AcpScreenRect;
using MonacoNative::Input::Native::CreateMemoryTsfTextStore;
using MonacoNative::Input::Native::CreateTsfTextStore;
using MonacoNative::Input::Native::IAcpTextStoreGeometry;
using MonacoNative::Input::Native::TsfCompositionPhase;
using MonacoNative::Input::Native::TsfDocumentSession;
using MonacoNative::Input::Native::TsfTextStore;
using MonacoNative::Input::Native::TextModelAcpTextStoreBackend;
using MonacoNative::Core::CursorModel;
using MonacoNative::Core::TextModel;

namespace
{
    class TestGeometry final : public IAcpTextStoreGeometry
    {
    public:
        bool HasLayout() const noexcept override { return true; }

        bool GetAcpFromScreenPoint(AcpScreenPoint point, std::uint32_t, std::int32_t& acp) const noexcept override
        {
            if (point.x < 10 || point.x > 110 || point.y < 20 || point.y > 60)
            {
                return false;
            }
            acp = static_cast<std::int32_t>((point.x - 10) / 10);
            return true;
        }

        bool GetTextScreenRect(
            std::int32_t start,
            std::int32_t end,
            AcpScreenRect& rect,
            bool& clipped) const noexcept override
        {
            rect = AcpScreenRect{10 + start * 10, 20, 10 + end * 10, 40};
            clipped = false;
            return true;
        }

        bool GetScreenRect(AcpScreenRect& rect) const noexcept override
        {
            rect = AcpScreenRect{10, 20, 210, 220};
            return true;
        }
    };

    class TestSink final : public ITextStoreACPSink
    {
    public:
        std::function<HRESULT(DWORD)> onLockGranted;
        std::atomic<ULONG> textChangeCount{0};
        std::atomic<ULONG> selectionChangeCount{0};
        TS_TEXTCHANGE lastTextChange{};

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) noexcept override
        {
            if (!object)
            {
                return E_POINTER;
            }
            *object = nullptr;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITextStoreACPSink))
            {
                *object = static_cast<ITextStoreACPSink*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++refs_; }
        ULONG STDMETHODCALLTYPE Release() noexcept override
        {
            const auto value = --refs_;
            if (value == 0)
            {
                delete this;
            }
            return value;
        }

        HRESULT STDMETHODCALLTYPE OnTextChange(DWORD, const TS_TEXTCHANGE* change) noexcept override
        {
            ++textChangeCount;
            if (change)
            {
                lastTextChange = *change;
            }
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnSelectionChange() noexcept override
        {
            ++selectionChangeCount;
            return S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnLayoutChange(TsLayoutCode, TsViewCookie) noexcept override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnStatusChange(DWORD) noexcept override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnAttrsChange(LONG, LONG, ULONG, const TS_ATTRID*) noexcept override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnLockGranted(DWORD flags) noexcept override
        {
            return onLockGranted ? onLockGranted(flags) : S_OK;
        }
        HRESULT STDMETHODCALLTYPE OnStartEditTransaction() noexcept override { return S_OK; }
        HRESULT STDMETHODCALLTYPE OnEndEditTransaction() noexcept override { return S_OK; }

    private:
        virtual ~TestSink() = default;
        std::atomic<ULONG> refs_{1};
    };

    bool Check(bool value, const char* expression)
    {
        if (!value)
        {
            std::cerr << "check failed: " << expression << '\n';
        }
        return value;
    }


    bool RunTextModelBackedStoreTest()
    {
        auto model = std::make_shared<TextModel>(u"one two three");
        auto backend = std::make_shared<TextModelAcpTextStoreBackend>(model);
        ITextStoreACP2* store = nullptr;
        if (FAILED(CreateTsfTextStore(backend, &store)) || !store)
        {
            std::cerr << "CreateTsfTextStore(TextModel) failed\n";
            return false;
        }

        auto* sink = new TestSink();
        bool ok = SUCCEEDED(store->AdviseSink(IID_ITextStoreACPSink, sink, TS_AS_TEXT_CHANGE | TS_AS_SEL_CHANGE));
        sink->onLockGranted = [&](DWORD flags) -> HRESULT
        {
            if ((flags & TS_LF_READWRITE) != TS_LF_READWRITE)
            {
                return S_OK;
            }

            TS_SELECTION_ACP selection{};
            selection.acpStart = 4;
            selection.acpEnd = 7;
            selection.style.ase = TS_AE_END;
            if (FAILED(store->SetSelection(1, &selection)))
            {
                ok = false;
                return E_FAIL;
            }

            const wchar_t replacement[] = L"TSF";
            TS_TEXTCHANGE change{};
            if (FAILED(store->InsertTextAtSelection(TF_IAS_NOQUERY, replacement, 3, nullptr, nullptr, &change)))
            {
                ok = false;
                return E_FAIL;
            }
            ok = ok && Check(change.acpStart == 4, "TextModel change start == 4");
            ok = ok && Check(change.acpOldEnd == 7, "TextModel change old end == 7");
            ok = ok && Check(change.acpNewEnd == 7, "TextModel change new end == 7");
            return S_OK;
        };

        HRESULT session{};
        if (FAILED(store->RequestLock(TS_LF_READWRITE | TS_LF_SYNC, &session)) || FAILED(session))
        {
            ok = false;
        }
        ok = ok && Check(model->GetValue() == std::u16string(u"one TSF three"), "TSF edit reached TextModel");
        ok = ok && Check(sink->textChangeCount.load() == 0, "TSF-originated edit does not self-notify sink");
        ok = ok && Check(model->CanUndo(), "TSF TextModel edit is undoable");
        ok = ok && Check(model->Undo(), "TSF TextModel edit undo succeeds");
        ok = ok && Check(model->GetValue() == std::u16string(u"one two three"), "undo restores TextModel");
        ok = ok && Check(sink->textChangeCount.load() == 1, "external undo notifies TSF sink");
        ok = ok && Check(sink->lastTextChange.acpStart == 4, "undo change start == 4");
        ok = ok && Check(sink->lastTextChange.acpOldEnd == 7, "undo change old end == 7");
        ok = ok && Check(sink->lastTextChange.acpNewEnd == 7, "undo change new end == 7");

        model->ApplyOffsetEdits({MonacoNative::Core::OffsetEdit{0, 0, u"!"}});
        ok = ok && Check(sink->textChangeCount.load() == 2, "external model edit notifies TSF sink");
        ok = ok && Check(sink->lastTextChange.acpStart == 0, "external insert start == 0");
        ok = ok && Check(sink->lastTextChange.acpOldEnd == 0, "external insert old end == 0");
        ok = ok && Check(sink->lastTextChange.acpNewEnd == 1, "external insert new end == 1");

        if (SUCCEEDED(store->UnadviseSink(sink)))
        {
            // expected
        }
        else
        {
            ok = false;
        }
        sink->Release();
        store->Release();
        return ok;
    }
    bool RunGeometryAndCompositionTest()
    {
        auto backend = std::make_shared<MonacoNative::Input::Native::MemoryAcpTextStoreBackend>(u"abcdefghij");
        auto geometry = std::make_shared<TestGeometry>();
        ITextStoreACP2* store = nullptr;
        if (FAILED(CreateTsfTextStore(backend, geometry, &store)) || !store)
        {
            std::cerr << "CreateTsfTextStore(geometry) failed\n";
            return false;
        }

        auto* sink = new TestSink();
        bool ok = SUCCEEDED(store->AdviseSink(IID_ITextStoreACPSink, sink, TS_AS_TEXT_CHANGE | TS_AS_SEL_CHANGE));

        LONG mapped = -1;
        POINT point{35, 30};
        ok = ok && Check(SUCCEEDED(store->GetACPFromPoint(1, &point, GXFPF_NEAREST, &mapped)), "GetACPFromPoint succeeds");
        ok = ok && Check(mapped == 2, "point maps to ACP 2");

        RECT screen{};
        ok = ok && Check(SUCCEEDED(store->GetScreenExt(1, &screen)), "GetScreenExt succeeds");
        ok = ok && Check(screen.left == 10 && screen.top == 20 && screen.right == 210 && screen.bottom == 220, "screen rect matches provider");

        sink->onLockGranted = [&](DWORD flags) -> HRESULT
        {
            if ((flags & TS_LF_READ) == 0)
            {
                return S_OK;
            }
            RECT rect{};
            BOOL clipped = TRUE;
            const auto hr = store->GetTextExt(1, 2, 5, &rect, &clipped);
            ok = ok && Check(SUCCEEDED(hr), "GetTextExt succeeds under read lock");
            ok = ok && Check(rect.left == 30 && rect.right == 60, "text rect maps ACP range");
            ok = ok && Check(clipped == FALSE, "text rect not clipped");

            RECT caretRect{};
            BOOL caretClipped = TRUE;
            const auto caretHr = store->GetTextExt(1, 3, 3, &caretRect, &caretClipped);
            ok = ok && Check(SUCCEEDED(caretHr), "GetTextExt accepts collapsed caret range");
            return S_OK;
        };
        HRESULT session{};
        ok = ok && Check(SUCCEEDED(store->RequestLock(TS_LF_READ | TS_LF_SYNC, &session)) && SUCCEEDED(session), "read lock geometry session succeeds");

        auto* concrete = dynamic_cast<TsfTextStore*>(store);
        ok = ok && Check(concrete != nullptr, "store exposes concrete composition sink in test");
        if (concrete)
        {
            std::size_t compositionEvents = 0;
            TsfCompositionPhase lastPhase = TsfCompositionPhase::Started;
            concrete->SetCompositionHandler([&](const auto& event)
            {
                ++compositionEvents;
                lastPhase = event.phase;
            });
            BOOL allowed = FALSE;
            ok = ok && Check(SUCCEEDED(concrete->OnStartComposition(nullptr, &allowed)) && allowed == TRUE, "composition start accepted");
            ok = ok && Check(concrete->IsCompositionActive(), "composition active after start");
            ok = ok && Check(SUCCEEDED(concrete->OnUpdateComposition(nullptr, nullptr)), "composition update accepted");
            ok = ok && Check(SUCCEEDED(concrete->OnEndComposition(nullptr)), "composition end accepted");
            ok = ok && Check(!concrete->IsCompositionActive(), "composition inactive after end");
            ok = ok && Check(compositionEvents == 3, "composition handler saw start/update/end");
            ok = ok && Check(lastPhase == TsfCompositionPhase::Ended, "last composition phase ended");
        }

        (void)store->UnadviseSink(sink);
        sink->Release();
        store->Release();
        return ok;
    }

    bool RunDocumentSessionLifecycleTest()
    {
        const auto init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        const bool shouldUninitialize = SUCCEEDED(init);
        if (FAILED(init) && init != RPC_E_CHANGED_MODE)
        {
            std::cerr << "CoInitializeEx failed\n";
            return false;
        }

        bool ok = true;
        auto model = std::make_shared<TextModel>(u"native TSF session");
        auto cursor = std::make_shared<CursorModel>(model);
        auto backend = std::make_shared<TextModelAcpTextStoreBackend>(model, cursor);
        auto geometry = std::make_shared<TestGeometry>();
        std::unique_ptr<TsfDocumentSession> session;
        const auto hr = TsfDocumentSession::Create(backend, geometry, session);
        ok = ok && Check(SUCCEEDED(hr) && session != nullptr, "TsfDocumentSession::Create succeeds");
        if (session)
        {
            ok = ok && Check(session->ThreadManager() != nullptr, "thread manager exists");
            ok = ok && Check(session->DocumentManager() != nullptr, "document manager exists");
            ok = ok && Check(session->Context() != nullptr, "context exists");
            ok = ok && Check(session->TextStore() != nullptr, "text store exists");
            ok = ok && Check(session->ClientId() != TF_CLIENTID_NULL, "client id activated");
            ok = ok && Check(SUCCEEDED(session->SetFocused()), "document manager receives TSF focus");
            BOOL eaten = TRUE;
            const auto downHr = session->RouteKeyDown('A', 1, &eaten);
            ok = ok && Check(downHr == S_OK || downHr == S_FALSE, "keystroke manager accepts TestKeyDown/KeyDown route");
            if (downHr != S_OK) ok = ok && Check(eaten == FALSE, "unclaimed keydown remains uneaten");
            eaten = TRUE;
            const auto upHr = session->RouteKeyUp('A', static_cast<LPARAM>((1u << 30) | (1u << 31) | 1u), &eaten);
            ok = ok && Check(upHr == S_OK || upHr == S_FALSE, "keystroke manager accepts TestKeyUp/KeyUp route");
            if (upHr != S_OK) ok = ok && Check(eaten == FALSE, "unclaimed keyup remains uneaten");
        }
        session.reset();
        if (shouldUninitialize)
        {
            CoUninitialize();
        }
        return ok;
    }

}

int main()
{
    ITextStoreACP2* store = nullptr;
    if (FAILED(CreateMemoryTsfTextStore(u"alpha beta", &store)) || !store)
    {
        std::cerr << "CreateMemoryTsfTextStore failed\n";
        return 1;
    }

    auto* sink = new TestSink();
    if (FAILED(store->AdviseSink(IID_ITextStoreACPSink, sink, TS_AS_TEXT_CHANGE | TS_AS_SEL_CHANGE)))
    {
        std::cerr << "AdviseSink failed\n";
        sink->Release();
        store->Release();
        return 1;
    }

    bool ok = true;
    sink->onLockGranted = [&](DWORD flags) -> HRESULT
    {
        if ((flags & TS_LF_READWRITE) == TS_LF_READWRITE)
        {
            TS_SELECTION_ACP selection{};
            selection.acpStart = 6;
            selection.acpEnd = 10;
            selection.style.ase = TS_AE_END;
            selection.style.fInterimChar = FALSE;
            if (FAILED(store->SetSelection(1, &selection)))
            {
                ok = false;
                return E_FAIL;
            }

            TS_TEXTCHANGE change{};
            const wchar_t replacement[] = L"TSF";
            if (FAILED(store->InsertTextAtSelection(TF_IAS_NOQUERY, replacement, 3, nullptr, nullptr, &change)))
            {
                ok = false;
                return E_FAIL;
            }
            ok = ok && Check(change.acpStart == 6, "change.acpStart == 6");
            ok = ok && Check(change.acpOldEnd == 10, "change.acpOldEnd == 10");
            ok = ok && Check(change.acpNewEnd == 9, "change.acpNewEnd == 9");
            return S_OK;
        }

        LONG end = -1;
        if (FAILED(store->GetEndACP(&end)))
        {
            ok = false;
            return E_FAIL;
        }
        ok = ok && Check(end == 10, "initial end == 10");
        wchar_t buffer[16]{};
        ULONG copied{};
        LONG next{};
        if (FAILED(store->GetText(0, -1, buffer, 16, &copied, nullptr, 0, nullptr, &next)))
        {
            ok = false;
            return E_FAIL;
        }
        ok = ok && Check(copied == 10, "copied == 10");
        ok = ok && Check(std::wstring(buffer, buffer + copied) == L"alpha beta", "read text matches");
        return S_OK;
    };

    HRESULT session{};
    if (FAILED(store->RequestLock(TS_LF_READ | TS_LF_SYNC, &session)) || FAILED(session))
    {
        ok = false;
    }
    if (FAILED(store->RequestLock(TS_LF_READWRITE | TS_LF_SYNC, &session)) || FAILED(session))
    {
        ok = false;
    }

    sink->onLockGranted = [&](DWORD) -> HRESULT
    {
        wchar_t buffer[16]{};
        ULONG copied{};
        LONG next{};
        if (FAILED(store->GetText(0, -1, buffer, 16, &copied, nullptr, 0, nullptr, &next)))
        {
            ok = false;
            return E_FAIL;
        }
        ok = ok && Check(std::wstring(buffer, buffer + copied) == L"alpha TSF", "inserted text matches");
        return S_OK;
    };
    if (FAILED(store->RequestLock(TS_LF_READ | TS_LF_SYNC, &session)) || FAILED(session))
    {
        ok = false;
    }

    ok = ok && SUCCEEDED(store->UnadviseSink(sink));
    sink->Release();
    store->Release();

    ok = ok && RunTextModelBackedStoreTest();
    ok = ok && RunGeometryAndCompositionTest();
    ok = ok && RunDocumentSessionLifecycleTest();

    if (!ok)
    {
        return 1;
    }
    std::cout << "NativeInputTsfTests: PASS\n";
    return 0;
}

#endif
