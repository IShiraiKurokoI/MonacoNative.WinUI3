#ifdef _WIN32

#include "MonacoNative/Input/Native/TsfDocumentSession.h"

#include <new>
#include <utility>

namespace MonacoNative::Input::Native
{
    TsfDocumentSession::TsfDocumentSession(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        std::shared_ptr<IAcpTextStoreGeometry> geometry) noexcept
        : ownerThreadId_(GetCurrentThreadId()),
          backend_(std::move(backend)),
          geometry_(std::move(geometry))
    {
    }

    TsfDocumentSession::~TsfDocumentSession()
    {
        Shutdown();
    }

    HRESULT TsfDocumentSession::Create(
        std::shared_ptr<IAcpTextStoreBackend> backend,
        std::shared_ptr<IAcpTextStoreGeometry> geometry,
        std::unique_ptr<TsfDocumentSession>& session) noexcept
    {
        session.reset();
        if (!backend)
        {
            return E_INVALIDARG;
        }

        auto candidate = std::unique_ptr<TsfDocumentSession>(
            new (std::nothrow) TsfDocumentSession(std::move(backend), std::move(geometry)));
        if (!candidate)
        {
            return E_OUTOFMEMORY;
        }

        const auto hr = candidate->Initialize();
        if (FAILED(hr))
        {
            return hr;
        }
        session = std::move(candidate);
        return S_OK;
    }

    HRESULT TsfDocumentSession::Initialize() noexcept
    {
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }

        auto hr = CoCreateInstance(
            CLSID_TF_ThreadMgr,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_ITfThreadMgr,
            reinterpret_cast<void**>(&threadManager_));
        if (FAILED(hr))
        {
            return hr;
        }

        hr = threadManager_->Activate(&clientId_);
        if (FAILED(hr))
        {
            Shutdown();
            return hr;
        }
        activated_ = true;

        hr = threadManager_->QueryInterface(
            IID_ITfKeystrokeMgr,
            reinterpret_cast<void**>(&keystrokeManager_));
        if (FAILED(hr))
        {
            Shutdown();
            return hr;
        }

        hr = threadManager_->CreateDocumentMgr(&documentManager_);
        if (FAILED(hr))
        {
            Shutdown();
            return hr;
        }

        textStore_ = new (std::nothrow) TsfTextStore(backend_, geometry_);
        if (!textStore_)
        {
            Shutdown();
            return E_OUTOFMEMORY;
        }

        hr = documentManager_->CreateContext(
            clientId_,
            0,
            static_cast<ITextStoreACP2*>(textStore_),
            &context_,
            &editCookie_);
        if (FAILED(hr))
        {
            Shutdown();
            return hr;
        }

        hr = documentManager_->Push(context_);
        if (FAILED(hr))
        {
            Shutdown();
            return hr;
        }
        pushed_ = true;
        return S_OK;
    }

    HRESULT TsfDocumentSession::SetFocused() noexcept
    {
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }
        if (!threadManager_ || !documentManager_)
        {
            return E_UNEXPECTED;
        }
        return threadManager_->SetFocus(documentManager_);
    }

    HRESULT TsfDocumentSession::AssociateFocus(HWND hwnd) noexcept
    {
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }
        if (!hwnd || !threadManager_ || !documentManager_)
        {
            return E_INVALIDARG;
        }
        if (associatedHwnd_ == hwnd)
        {
            return S_OK;
        }
        if (associatedHwnd_)
        {
            const auto clearHr = ClearFocusAssociation();
            if (FAILED(clearHr))
            {
                return clearHr;
            }
        }

        ITfDocumentMgr* previous = nullptr;
        const auto hr = threadManager_->AssociateFocus(hwnd, documentManager_, &previous);
        if (FAILED(hr))
        {
            if (previous)
            {
                previous->Release();
            }
            return hr;
        }
        associatedHwnd_ = hwnd;
        previousDocumentManager_ = previous;
        return S_OK;
    }

    HRESULT TsfDocumentSession::ClearFocusAssociation() noexcept
    {
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }
        if (!associatedHwnd_)
        {
            return S_OK;
        }
        if (!threadManager_)
        {
            return E_UNEXPECTED;
        }

        ITfDocumentMgr* displaced = nullptr;
        const auto hr = threadManager_->AssociateFocus(
            associatedHwnd_,
            previousDocumentManager_,
            &displaced);
        if (displaced)
        {
            displaced->Release();
        }
        if (FAILED(hr))
        {
            return hr;
        }
        if (previousDocumentManager_)
        {
            previousDocumentManager_->Release();
            previousDocumentManager_ = nullptr;
        }
        associatedHwnd_ = nullptr;
        return S_OK;
    }


    HRESULT TsfDocumentSession::RouteKeyDown(WPARAM wParam, LPARAM lParam, BOOL* eaten) noexcept
    {
        if (!eaten)
        {
            return E_POINTER;
        }
        *eaten = FALSE;
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }
        if (!keystrokeManager_)
        {
            return E_UNEXPECTED;
        }

        BOOL wantsKey = FALSE;
        const auto testHr = keystrokeManager_->TestKeyDown(wParam, lParam, &wantsKey);
        if (testHr != S_OK || !wantsKey)
        {
            return testHr;
        }

        BOOL handled = FALSE;
        const auto keyHr = keystrokeManager_->KeyDown(wParam, lParam, &handled);
        if (SUCCEEDED(keyHr))
        {
            *eaten = handled;
        }
        return keyHr;
    }

    HRESULT TsfDocumentSession::RouteKeyUp(WPARAM wParam, LPARAM lParam, BOOL* eaten) noexcept
    {
        if (!eaten)
        {
            return E_POINTER;
        }
        *eaten = FALSE;
        if (!IsOwnerThread())
        {
            return RPC_E_WRONG_THREAD;
        }
        if (!keystrokeManager_)
        {
            return E_UNEXPECTED;
        }

        BOOL wantsKey = FALSE;
        const auto testHr = keystrokeManager_->TestKeyUp(wParam, lParam, &wantsKey);
        if (testHr != S_OK || !wantsKey)
        {
            return testHr;
        }

        BOOL handled = FALSE;
        const auto keyHr = keystrokeManager_->KeyUp(wParam, lParam, &handled);
        if (SUCCEEDED(keyHr))
        {
            *eaten = handled;
        }
        return keyHr;
    }

    void TsfDocumentSession::SetCompositionHandler(TsfTextStore::CompositionHandler handler)
    {
        if (textStore_)
        {
            textStore_->SetCompositionHandler(std::move(handler));
        }
    }

    bool TsfDocumentSession::IsOwnerThread() const noexcept
    {
        return ownerThreadId_ == GetCurrentThreadId();
    }

    void TsfDocumentSession::Shutdown() noexcept
    {
        // TSF requires Push/Pop and Activate/Deactivate to be paired on the
        // same thread. Production integration owns this session on the WinUI
        // UI thread; if destruction is accidentally routed elsewhere, avoid
        // issuing invalid TSF calls and release only ordinary COM references.
        const bool onOwnerThread = IsOwnerThread();

        if (associatedHwnd_ && onOwnerThread)
        {
            (void)ClearFocusAssociation();
        }

        if (documentManager_ && pushed_ && onOwnerThread)
        {
            (void)documentManager_->Pop(TF_POPF_ALL);
            pushed_ = false;
        }

        if (context_)
        {
            context_->Release();
            context_ = nullptr;
        }
        if (textStore_)
        {
            textStore_->Release();
            textStore_ = nullptr;
        }
        if (previousDocumentManager_)
        {
            previousDocumentManager_->Release();
            previousDocumentManager_ = nullptr;
        }
        associatedHwnd_ = nullptr;
        if (documentManager_)
        {
            documentManager_->Release();
            documentManager_ = nullptr;
        }
        if (keystrokeManager_)
        {
            keystrokeManager_->Release();
            keystrokeManager_ = nullptr;
        }
        if (threadManager_)
        {
            if (activated_ && onOwnerThread)
            {
                (void)threadManager_->Deactivate();
                activated_ = false;
            }
            threadManager_->Release();
            threadManager_ = nullptr;
        }
        clientId_ = TF_CLIENTID_NULL;
        editCookie_ = TF_INVALID_EDIT_COOKIE;
    }
}

#endif
