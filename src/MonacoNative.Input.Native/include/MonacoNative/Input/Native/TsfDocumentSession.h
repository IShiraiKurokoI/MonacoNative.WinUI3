#pragma once

#ifdef _WIN32

#include "MonacoNative/Input/Native/AcpTextStoreBackend.h"
#include "MonacoNative/Input/Native/AcpTextStoreGeometry.h"
#include "MonacoNative/Input/Native/TsfTextStore.h"

#include <Windows.h>
#include <msctf.h>

#include <memory>

namespace MonacoNative::Input::Native
{
    // Owns one TSF application-side document/context stack for an editor.
    // Creation and destruction must happen on the same UI thread, matching
    // ITfThreadMgr::Activate/Deactivate and ITfDocumentMgr::Push/Pop rules.
    class TsfDocumentSession final
    {
    public:
        static HRESULT Create(
            std::shared_ptr<IAcpTextStoreBackend> backend,
            std::shared_ptr<IAcpTextStoreGeometry> geometry,
            std::unique_ptr<TsfDocumentSession>& session) noexcept;

        ~TsfDocumentSession();

        TsfDocumentSession(const TsfDocumentSession&) = delete;
        TsfDocumentSession& operator=(const TsfDocumentSession&) = delete;
        TsfDocumentSession(TsfDocumentSession&&) = delete;
        TsfDocumentSession& operator=(TsfDocumentSession&&) = delete;

        HRESULT SetFocused() noexcept;
        HRESULT AssociateFocus(HWND hwnd) noexcept;
        HRESULT ClearFocusAssociation() noexcept;
        HRESULT RouteKeyDown(WPARAM wParam, LPARAM lParam, BOOL* eaten) noexcept;
        HRESULT RouteKeyUp(WPARAM wParam, LPARAM lParam, BOOL* eaten) noexcept;
        void SetCompositionHandler(TsfTextStore::CompositionHandler handler);

        [[nodiscard]] ITfThreadMgr* ThreadManager() const noexcept { return threadManager_; }
        [[nodiscard]] ITfDocumentMgr* DocumentManager() const noexcept { return documentManager_; }
        [[nodiscard]] ITfContext* Context() const noexcept { return context_; }
        [[nodiscard]] TsfTextStore* TextStore() const noexcept { return textStore_; }
        [[nodiscard]] TfClientId ClientId() const noexcept { return clientId_; }
        [[nodiscard]] TfEditCookie TextStoreEditCookie() const noexcept { return editCookie_; }
        [[nodiscard]] DWORD OwnerThreadId() const noexcept { return ownerThreadId_; }

    private:
        TsfDocumentSession(
            std::shared_ptr<IAcpTextStoreBackend> backend,
            std::shared_ptr<IAcpTextStoreGeometry> geometry) noexcept;

        HRESULT Initialize() noexcept;
        void Shutdown() noexcept;
        [[nodiscard]] bool IsOwnerThread() const noexcept;

        DWORD ownerThreadId_{};
        std::shared_ptr<IAcpTextStoreBackend> backend_{};
        std::shared_ptr<IAcpTextStoreGeometry> geometry_{};
        ITfThreadMgr* threadManager_{};
        ITfKeystrokeMgr* keystrokeManager_{};
        ITfDocumentMgr* documentManager_{};
        ITfContext* context_{};
        TsfTextStore* textStore_{};
        TfClientId clientId_{TF_CLIENTID_NULL};
        TfEditCookie editCookie_{TF_INVALID_EDIT_COOKIE};
        bool activated_{};
        bool pushed_{};
        HWND associatedHwnd_{};
        ITfDocumentMgr* previousDocumentManager_{};
    };
}

#endif
