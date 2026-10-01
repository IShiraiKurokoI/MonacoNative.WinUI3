#pragma once
#include "EditorHost.g.h"
#include "MonacoNative/Core/TextModel.h"

namespace winrt::MonacoNative::WinRT::implementation
{
    struct EditorHost : EditorHostT<EditorHost>
    {
        EditorHost();
        EditorHost(hstring const& text, hstring const& languageId);

        hstring Text() const;
        void Text(hstring const& value);
        hstring LanguageId() const;
        void LanguageId(hstring const& value);
        uint64_t Length() const noexcept;
        uint64_t LineCount() const noexcept;
        uint64_t VersionId() const noexcept;
        bool CanUndo() const noexcept;
        bool CanRedo() const noexcept;
        void Replace(uint64_t startOffset, uint64_t endOffset, hstring const& text);
        bool Undo();
        bool Redo();
        void PushUndoStop();

    private:
        std::shared_ptr<::MonacoNative::Core::TextModel> model_;
    };
}

namespace winrt::MonacoNative::WinRT::factory_implementation
{
    struct EditorHost : EditorHostT<EditorHost, implementation::EditorHost>
    {
    };
}
