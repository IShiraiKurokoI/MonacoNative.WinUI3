#include "pch.h"
#include "EditorHost.h"
#include "EditorHost.g.cpp"

#include <stdexcept>

namespace
{
    std::u16string ToUtf16(winrt::hstring const& value)
    {
        std::u16string result;
        result.reserve(value.size());
        auto const* data = value.c_str();
        for (uint32_t i = 0; i < value.size(); ++i)
        {
            result.push_back(static_cast<char16_t>(data[i]));
        }
        return result;
    }

    winrt::hstring ToHString(std::u16string const& value)
    {
        std::wstring text;
        text.reserve(value.size());
        for (char16_t ch : value)
        {
            text.push_back(static_cast<wchar_t>(ch));
        }
        return winrt::hstring(text);
    }
}

namespace winrt::MonacoNative::WinRT::implementation
{
    EditorHost::EditorHost()
        : model_(std::make_shared<::MonacoNative::Core::TextModel>())
    {
    }

    EditorHost::EditorHost(hstring const& text, hstring const& languageId)
        : model_(std::make_shared<::MonacoNative::Core::TextModel>(ToUtf16(text), winrt::to_string(languageId)))
    {
    }

    hstring EditorHost::Text() const
    {
        return ToHString(model_->GetValue());
    }

    void EditorHost::Text(hstring const& value)
    {
        model_->SetValue(ToUtf16(value));
    }

    hstring EditorHost::LanguageId() const
    {
        return winrt::to_hstring(std::string(model_->LanguageId()));
    }

    void EditorHost::LanguageId(hstring const& value)
    {
        model_->SetLanguageId(winrt::to_string(value));
    }

    uint64_t EditorHost::Length() const noexcept
    {
        return static_cast<uint64_t>(model_->Length());
    }

    uint64_t EditorHost::LineCount() const noexcept
    {
        return static_cast<uint64_t>(model_->GetLineCount());
    }

    uint64_t EditorHost::VersionId() const noexcept
    {
        return model_->VersionId();
    }

    bool EditorHost::CanUndo() const noexcept
    {
        return model_->CanUndo();
    }

    bool EditorHost::CanRedo() const noexcept
    {
        return model_->CanRedo();
    }

    void EditorHost::Replace(uint64_t startOffset, uint64_t endOffset, hstring const& text)
    {
        if (startOffset > endOffset || endOffset > model_->Length())
        {
            throw hresult_out_of_bounds();
        }
        model_->ApplyOffsetEdits({ ::MonacoNative::Core::OffsetEdit{
            static_cast<std::size_t>(startOffset),
            static_cast<std::size_t>(endOffset),
            ToUtf16(text) } });
    }

    bool EditorHost::Undo()
    {
        return model_->Undo();
    }

    bool EditorHost::Redo()
    {
        return model_->Redo();
    }

    void EditorHost::PushUndoStop()
    {
        model_->PushUndoStop();
    }
}
