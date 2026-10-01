#include "MonacoNative.Core.Native.h"
#include "MonacoNative/Core/TextModel.h"
#include "MonacoNative/Core/Commands.h"
#include "MonacoNative/Core/DiffEngine.h"
#include "MonacoNative/Core/TextMeasurer.h"
#include "MonacoNative/Core/ViewModel.h"
#include "MonacoNative/Core/UnicodeSegmentation.h"

#ifdef _WIN32
#include "MonacoNative/Input/Native/TsfDocumentSession.h"
#include "MonacoNative/Input/Native/AcpTextStoreGeometry.h"
#include "MonacoNative/Input/Native/AcpTextStoreBackend.h"
#include <Windows.h>
#include <unordered_map>
#endif

#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace
{
    using MonacoNative::Core::EndOfLineSequence;
    using MonacoNative::Core::OffsetEdit;
    using MonacoNative::Core::Position;
    using MonacoNative::Core::TextModel;
    using MonacoNative::Core::ViewModel;
    using MonacoNative::Core::ITextMeasurer;
    using MonacoNative::Core::HiddenArea;
    using MonacoNative::Core::InjectedTextAffinity;
    using MonacoNative::Core::RevealStrategy;
    using MonacoNative::Core::Range;
    using MonacoNative::Core::ContextKeyService;
    using MonacoNative::Core::KeybindingResolver;
    using MonacoNative::Core::KeybindingRule;
    using MonacoNative::Core::KeyStroke;
    using MonacoNative::Core::KeybindingResultKind;
    using MonacoNative::Core::DiffEngine;
    using MonacoNative::Core::DiffOptions;
    using MonacoNative::Core::DiffResult;

    struct ModelHandle
    {
        std::shared_ptr<TextModel> model;
    };

    class CallbackTextMeasurer final : public ITextMeasurer
    {
    public:
        CallbackTextMeasurer(mn_measure_text_callback callback, void* context) noexcept
            : callback_(callback), context_(context)
        {
        }

        [[nodiscard]] double Measure(std::u16string_view text) const override
        {
            if (!callback_)
            {
                return static_cast<double>(text.size());
            }
            const auto measured = callback_(context_, reinterpret_cast<const mn_char16*>(text.data()), text.size());
            return measured >= 0.0 ? measured : 0.0;
        }

    private:
        mn_measure_text_callback callback_{};
        void* context_{};
    };

    struct ViewHandle
    {
        std::shared_ptr<TextModel> model;
        std::unique_ptr<ViewModel> view;
        std::shared_ptr<ITextMeasurer> callbackMeasurer;
    };

    struct KeybindingHandle
    {
        ContextKeyService context;
        KeybindingResolver resolver;
        std::string resolvedCommand;
        std::string resolvedChord;
    };

    struct DiffHandle
    {
        DiffResult result;
    };

    ModelHandle* AsHandle(mn_model_handle handle) noexcept
    {
        return static_cast<ModelHandle*>(handle);
    }

    ViewHandle* AsViewHandle(mn_view_handle handle) noexcept
    {
        return static_cast<ViewHandle*>(handle);
    }

    KeybindingHandle* AsKeybindingHandle(mn_keybinding_handle handle) noexcept
    {
        return static_cast<KeybindingHandle*>(handle);
    }

    DiffHandle* AsDiffHandle(mn_diff_handle handle) noexcept
    {
        return static_cast<DiffHandle*>(handle);
    }

    std::u16string ToUtf16(const mn_char16* value, std::size_t length)
    {
        if (!value || length == 0) return {};
        std::u16string result;
        result.reserve(length);
        for (std::size_t i = 0; i < length; ++i)
            result.push_back(static_cast<char16_t>(value[i]));
        return result;
    }

    std::u16string ToUtf16(const mn_char16* value)
    {
        if (!value)
        {
            return {};
        }
        const auto length = std::char_traits<mn_char16>::length(value);
        std::u16string result;
        result.reserve(length);
        for (std::size_t i = 0; i < length; ++i)
        {
            result.push_back(static_cast<char16_t>(value[i]));
        }
        return result;
    }

    size_t CopyUtf16(std::u16string_view text, mn_char16* buffer, size_t bufferChars)
    {
        const size_t required = text.size() + 1;
        if (buffer && bufferChars)
        {
            const size_t count = (std::min)(text.size(), bufferChars - 1);
            for (std::size_t i = 0; i < count; ++i)
            {
                buffer[i] = static_cast<mn_char16>(text[i]);
            }
            buffer[count] = static_cast<mn_char16>(0);
        }
        return required;
    }

    size_t CopyUtf8(std::string_view text, char* buffer, size_t bufferBytes)
    {
        const size_t required = text.size() + 1;
        if (buffer && bufferBytes)
        {
            const size_t count = (std::min)(text.size(), bufferBytes - 1);
            std::copy_n(text.data(), count, buffer);
            buffer[count] = '\0';
        }
        return required;
    }

#ifdef _WIN32
    using MonacoNative::Input::Native::AcpExternalChangeEvent;
    using MonacoNative::Input::Native::AcpScreenPoint;
    using MonacoNative::Input::Native::AcpScreenRect;
    using MonacoNative::Input::Native::AcpSelection;
    using MonacoNative::Input::Native::AcpTextChange;
    using MonacoNative::Input::Native::IAcpTextStoreBackend;
    using MonacoNative::Input::Native::IAcpTextStoreGeometry;
    using MonacoNative::Input::Native::TsfCompositionEvent;
    using MonacoNative::Input::Native::TsfCompositionPhase;
    using MonacoNative::Input::Native::TsfDocumentSession;

    class HostAcpBackend final : public IAcpTextStoreBackend
    {
    public:
        HostAcpBackend(std::shared_ptr<TextModel> model, mn_tsf_callbacks callbacks)
            : model_(std::move(model)), callbacks_(callbacks)
        {
            modelToken_ = model_->OnDidChangeContent().Add([this](const MonacoNative::Core::ModelContentChangedEvent& event)
            {
                if (tsfEditDepth_ != 0 && callbacks_.model_changed)
                {
                    for (const auto& change : event.changes)
                    {
                        if (change.rangeOffset > static_cast<size_t>(INT32_MAX) ||
                            change.rangeLength > static_cast<size_t>(INT32_MAX) ||
                            change.text.size() > static_cast<size_t>(INT32_MAX))
                            continue;
                        const auto startOffset = static_cast<int32_t>(change.rangeOffset);
                        const auto oldEndOffset = static_cast<int32_t>(change.rangeOffset + change.rangeLength);
                        const auto newEndOffset = static_cast<int32_t>(change.rangeOffset + change.text.size());
                        const auto newEndPosition = model_->GetPositionAt(static_cast<size_t>(newEndOffset));
                        callbacks_.model_changed(
                            callbacks_.context,
                            startOffset,
                            oldEndOffset,
                            newEndOffset,
                            change.range.startLineNumber,
                            change.range.startColumn,
                            change.range.endLineNumber,
                            change.range.endColumn,
                            newEndPosition.lineNumber,
                            newEndPosition.column);
                    }
                }

                if (tsfEditDepth_ != 0 || handlers_.empty())
                    return;

                AcpExternalChangeEvent external{};
                external.textChanges.reserve(event.changes.size());
                for (const auto& change : event.changes)
                {
                    if (change.rangeOffset > static_cast<size_t>(INT32_MAX) ||
                        change.rangeLength > static_cast<size_t>(INT32_MAX) ||
                        change.text.size() > static_cast<size_t>(INT32_MAX) ||
                        change.rangeOffset + change.rangeLength > static_cast<size_t>(INT32_MAX) ||
                        change.rangeOffset + change.text.size() > static_cast<size_t>(INT32_MAX))
                        continue;
                    const auto start = static_cast<int32_t>(change.rangeOffset);
                    const auto oldEnd = static_cast<int32_t>(change.rangeOffset + change.rangeLength);
                    const auto newEnd = static_cast<int32_t>(change.rangeOffset + change.text.size());
                    external.textChanges.push_back({start, oldEnd, newEnd});
                }
                for (const auto& entry : handlers_)
                    entry.second(external);
            });
        }
        ~HostAcpBackend() override
        {
            if (model_ && modelToken_)
                model_->OnDidChangeContent().Remove(modelToken_);
        }

        int32_t Length() const noexcept override
        {
            return static_cast<int32_t>(std::min<size_t>(model_->Length(), INT32_MAX));
        }
        std::u16string GetText(int32_t start, int32_t end) const override
        {
            if (start < 0 || end < start || static_cast<size_t>(end) > model_->Length())
                return {};
            return model_->GetValue(static_cast<size_t>(start), static_cast<size_t>(end - start));
        }
        AcpSelection GetSelection() const noexcept override
        {
            int32_t start{}, end{}; int active{};
            if (callbacks_.get_selection &&
                callbacks_.get_selection(callbacks_.context, &start, &end, &active) != 0)
            {
                const auto length = Length();
                start = std::clamp(start, 0, length);
                end = std::clamp(end, start, length);
                return {start, end, active != 0, false};
            }
            return fallbackSelection_;
        }
        bool SetSelection(const AcpSelection& selection) noexcept override
        {
            if (selection.start < 0 || selection.end < selection.start || selection.end > Length())
                return false;
            fallbackSelection_ = selection;
            return !callbacks_.set_selection ||
                callbacks_.set_selection(callbacks_.context, selection.start, selection.end,
                    selection.activeEndAtStart ? 1 : 0) != 0;
        }
        bool IsReadOnly() const noexcept override { return readOnly_; }
        void SetReadOnly(bool value) noexcept { readOnly_ = value; }

        bool Replace(int32_t start, int32_t end, std::u16string_view text, AcpTextChange& change) override
        {
            if (readOnly_ || start < 0 || end < start || static_cast<size_t>(end) > model_->Length())
                return false;
            ++tsfEditDepth_;
            try
            {
                model_->ApplyOffsetEdits({OffsetEdit{static_cast<size_t>(start), static_cast<size_t>(end), std::u16string(text)}});
            }
            catch (...)
            {
                --tsfEditDepth_;
                throw;
            }
            --tsfEditDepth_;
            const auto newEnd = start + static_cast<int32_t>(text.size());
            change = {start, end, newEnd};
            const AcpSelection collapsed{newEnd, newEnd, false, false};
            fallbackSelection_ = collapsed;
            if (callbacks_.set_selection)
                (void)callbacks_.set_selection(callbacks_.context, newEnd, newEnd, 0);
            return true;
        }

        ExternalChangeToken AddExternalChangeHandler(ExternalChangeHandler handler) override
        {
            if (!handler) return 0;
            const auto token = nextToken_++;
            handlers_.emplace(token, std::move(handler));
            return token;
        }
        void RemoveExternalChangeHandler(ExternalChangeToken token) noexcept override { handlers_.erase(token); }
        void NotifySelectionChanged()
        {
            if (handlers_.empty()) return;
            AcpExternalChangeEvent event{};
            event.selectionChanged = true;
            for (const auto& entry : handlers_)
                entry.second(event);
        }

    private:
        std::shared_ptr<TextModel> model_;
        mn_tsf_callbacks callbacks_{};
        AcpSelection fallbackSelection_{};
        MonacoNative::Core::EventSource<MonacoNative::Core::ModelContentChangedEvent>::Token modelToken_{};
        ExternalChangeToken nextToken_{1};
        std::unordered_map<ExternalChangeToken, ExternalChangeHandler> handlers_;
        uint32_t tsfEditDepth_{};
        bool readOnly_{};
    };

    class HostGeometry final : public IAcpTextStoreGeometry
    {
    public:
        explicit HostGeometry(mn_tsf_callbacks callbacks) : callbacks_(callbacks) {}
        bool HasLayout() const noexcept override
        {
            return callbacks_.has_layout && callbacks_.has_layout(callbacks_.context) != 0;
        }
        bool GetAcpFromScreenPoint(AcpScreenPoint point, uint32_t flags, int32_t& acp) const noexcept override
        {
            return callbacks_.point_to_acp &&
                callbacks_.point_to_acp(callbacks_.context, point.x, point.y, flags, &acp) != 0;
        }
        bool GetTextScreenRect(int32_t start, int32_t end, AcpScreenRect& rect, bool& clipped) const noexcept override
        {
            if (!callbacks_.text_rect) return false;
            mn_tsf_rect value{}; int clippedValue{};
            if (!callbacks_.text_rect(callbacks_.context, start, end, &value, &clippedValue)) return false;
            rect = {value.left, value.top, value.right, value.bottom};
            clipped = clippedValue != 0;
            return true;
        }
        bool GetScreenRect(AcpScreenRect& rect) const noexcept override
        {
            if (!callbacks_.screen_rect) return false;
            mn_tsf_rect value{};
            if (!callbacks_.screen_rect(callbacks_.context, &value)) return false;
            rect = {value.left, value.top, value.right, value.bottom};
            return true;
        }
    private:
        mn_tsf_callbacks callbacks_{};
    };

    struct TsfHostHandle
    {
        HWND hwnd{};
        mn_tsf_callbacks callbacks{};
        std::shared_ptr<TextModel> model;
        bool ownsCompositionUndoTransaction{};
        std::shared_ptr<HostAcpBackend> backend;
        std::shared_ptr<HostGeometry> geometry;
        std::unique_ptr<TsfDocumentSession> session;
    };

    TsfHostHandle* AsTsfHost(mn_tsf_handle handle) noexcept
    {
        return static_cast<TsfHostHandle*>(handle);
    }
#endif
}

uint32_t MN_CALL mn_abi_version()
{
    return MN_ABI_VERSION_PACKED;
}

int MN_CALL mn_abi_get_info(mn_abi_info* info)
{
    if (!info || info->struct_size < sizeof(uint32_t))
    {
        return 0;
    }

    const mn_abi_info current{
        static_cast<uint32_t>(sizeof(mn_abi_info)),
        MN_ABI_VERSION_PACKED,
        MN_ABI_VERSION_MAJOR,
        MN_ABI_VERSION_MINOR,
        MN_ABI_VERSION_PATCH,
        static_cast<uint32_t>(sizeof(void*)),
        static_cast<uint32_t>(sizeof(mn_char16)),
        static_cast<uint32_t>(sizeof(mn_range)),
        static_cast<uint32_t>(sizeof(mn_bracket_match)),
        static_cast<uint32_t>(sizeof(mn_view_line_info)),
        static_cast<uint32_t>(sizeof(mn_hidden_area)),
        static_cast<uint32_t>(sizeof(mn_diff_change)),
        static_cast<uint32_t>(sizeof(mn_diff_inner_change)),
        static_cast<uint32_t>(sizeof(mn_diff_move)),
    };

    const auto bytes = std::min<std::size_t>(info->struct_size, sizeof(current));
    std::memcpy(info, &current, bytes);
    return 1;
}

mn_model_handle MN_CALL mn_model_create(const mn_char16* text, const char* language_id)
{
    try
    {
        auto result = std::make_unique<ModelHandle>();
        result->model = std::make_shared<TextModel>(ToUtf16(text), language_id ? language_id : "plaintext");
        return result.release();
    }
    catch (...)
    {
        return nullptr;
    }
}

void MN_CALL mn_model_destroy(mn_model_handle handle)
{
    delete AsHandle(handle);
}

void MN_CALL mn_diff_destroy(mn_diff_handle handle)
{
    delete AsDiffHandle(handle);
}


#ifdef _WIN32
mn_tsf_handle MN_CALL mn_tsf_create(mn_model_handle modelHandle, intptr_t hwndValue, const mn_tsf_callbacks* callbacks)
{
    auto* model = AsHandle(modelHandle);
    if (!model || !model->model || model->model->Length() > static_cast<size_t>(INT32_MAX) || !hwndValue || !callbacks ||
        callbacks->struct_size < sizeof(mn_tsf_callbacks))
        return nullptr;

    try
    {
        auto host = std::make_unique<TsfHostHandle>();
        host->hwnd = reinterpret_cast<HWND>(hwndValue);
        host->callbacks = *callbacks;
        host->model = model->model;
        host->backend = std::make_shared<HostAcpBackend>(host->model, host->callbacks);
        host->geometry = std::make_shared<HostGeometry>(host->callbacks);
        if (FAILED(TsfDocumentSession::Create(host->backend, host->geometry, host->session)) || !host->session)
            return nullptr;
        host->session->SetCompositionHandler([raw = host.get()](const TsfCompositionEvent& event)
        {
            if (raw->model)
            {
                if (event.phase == TsfCompositionPhase::Started &&
                    !raw->model->InUndoTransaction())
                {
                    raw->model->BeginUndoTransaction();
                    raw->ownsCompositionUndoTransaction = true;
                }
                else if (event.phase == TsfCompositionPhase::Ended &&
                         raw->ownsCompositionUndoTransaction)
                {
                    raw->model->EndUndoTransaction();
                    raw->ownsCompositionUndoTransaction = false;
                }
            }

            if (!raw->callbacks.composition) return;
            int32_t phase = 0;
            switch (event.phase)
            {
            case TsfCompositionPhase::Started: phase = 1; break;
            case TsfCompositionPhase::Updated: phase = 2; break;
            case TsfCompositionPhase::Ended: phase = 3; break;
            }
            raw->callbacks.composition(raw->callbacks.context, phase, event.start, event.end);
        });
        return host.release();
    }
    catch (...)
    {
        return nullptr;
    }
}

void MN_CALL mn_tsf_destroy(mn_tsf_handle handle)
{
    delete AsTsfHost(handle);
}

int MN_CALL mn_tsf_focus_enter(mn_tsf_handle handle)
{
    auto* host = AsTsfHost(handle);
    if (!host || !host->session) return 0;
    if (FAILED(host->session->AssociateFocus(host->hwnd))) return 0;
    return SUCCEEDED(host->session->SetFocused()) ? 1 : 0;
}

int MN_CALL mn_tsf_focus_leave(mn_tsf_handle handle)
{
    auto* host = AsTsfHost(handle);
    if (!host || !host->session) return 0;
    return SUCCEEDED(host->session->ClearFocusAssociation()) ? 1 : 0;
}

void MN_CALL mn_tsf_notify_selection_changed(mn_tsf_handle handle)
{
    auto* host = AsTsfHost(handle);
    if (host && host->backend) host->backend->NotifySelectionChanged();
}

void MN_CALL mn_tsf_set_read_only(mn_tsf_handle handle, int readOnly)
{
    auto* host = AsTsfHost(handle);
    if (host && host->backend) host->backend->SetReadOnly(readOnly != 0);
}

int MN_CALL mn_tsf_key_down(mn_tsf_handle handle, uint32_t virtualKey, uint32_t messageLParam)
{
    auto* host = AsTsfHost(handle);
    if (!host || !host->session) return 0;
    BOOL eaten = FALSE;
    const auto hr = host->session->RouteKeyDown(
        static_cast<WPARAM>(virtualKey),
        static_cast<LPARAM>(messageLParam),
        &eaten);
    return SUCCEEDED(hr) && eaten ? 1 : 0;
}

int MN_CALL mn_tsf_key_up(mn_tsf_handle handle, uint32_t virtualKey, uint32_t messageLParam)
{
    auto* host = AsTsfHost(handle);
    if (!host || !host->session) return 0;
    BOOL eaten = FALSE;
    const auto hr = host->session->RouteKeyUp(
        static_cast<WPARAM>(virtualKey),
        static_cast<LPARAM>(messageLParam),
        &eaten);
    return SUCCEEDED(hr) && eaten ? 1 : 0;
}
#endif

size_t MN_CALL mn_unicode_previous_grapheme_boundary(const mn_char16* text, size_t length, size_t offset)
{
    try { const auto value = ToUtf16(text, length); return MonacoNative::Core::Unicode::PreviousGraphemeBoundary(value, offset); } catch (...) { return 0; }
}

size_t MN_CALL mn_unicode_next_grapheme_boundary(const mn_char16* text, size_t length, size_t offset)
{
    try { const auto value = ToUtf16(text, length); return MonacoNative::Core::Unicode::NextGraphemeBoundary(value, offset); } catch (...) { return 0; }
}

size_t MN_CALL mn_unicode_previous_word_boundary(const mn_char16* text, size_t length, size_t offset)
{
    try { const auto value = ToUtf16(text, length); return MonacoNative::Core::Unicode::PreviousWordBoundary(value, offset); } catch (...) { return 0; }
}

size_t MN_CALL mn_unicode_next_word_boundary(const mn_char16* text, size_t length, size_t offset)
{
    try { const auto value = ToUtf16(text, length); return MonacoNative::Core::Unicode::NextWordBoundary(value, offset); } catch (...) { return 0; }
}

mn_diff_handle MN_CALL mn_diff_compute(
    const mn_char16* original,
    const mn_char16* modified,
    int ignore_trim_whitespace,
    int compute_moves)
{
    try
    {
        auto handle = std::make_unique<DiffHandle>();
        handle->result = DiffEngine::ComputeDiff(
            ToUtf16(original),
            ToUtf16(modified),
            DiffOptions{ignore_trim_whitespace != 0, compute_moves != 0});
        return handle.release();
    }
    catch (...)
    {
        return nullptr;
    }
}

size_t MN_CALL mn_diff_change_count(mn_diff_handle handle)
{
    const auto h = AsDiffHandle(handle);
    return h ? h->result.changes.size() : 0;
}

int MN_CALL mn_diff_get_change(mn_diff_handle handle, size_t index, mn_diff_change* change)
{
    try
    {
        const auto h = AsDiffHandle(handle);
        if (!h || !change || index >= h->result.changes.size()) return 0;
        const auto& value = h->result.changes[index];
        *change = mn_diff_change{
            value.originalStartLine,
            value.originalEndLineExclusive,
            value.modifiedStartLine,
            value.modifiedEndLineExclusive,
            value.innerChanges.size(),
        };
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_diff_get_inner_change(
    mn_diff_handle handle,
    size_t change_index,
    size_t inner_index,
    mn_diff_inner_change* change)
{
    try
    {
        const auto h = AsDiffHandle(handle);
        if (!h || !change || change_index >= h->result.changes.size()) return 0;
        const auto& parent = h->result.changes[change_index];
        if (inner_index >= parent.innerChanges.size()) return 0;
        const auto& value = parent.innerChanges[inner_index];
        *change = mn_diff_inner_change{
            value.originalLine,
            value.originalStartColumn,
            value.originalEndColumn,
            value.modifiedLine,
            value.modifiedStartColumn,
            value.modifiedEndColumn,
        };
        return 1;
    }
    catch (...) { return 0; }
}

size_t MN_CALL mn_diff_move_count(mn_diff_handle handle)
{
    const auto h = AsDiffHandle(handle);
    return h ? h->result.moves.size() : 0;
}

int MN_CALL mn_diff_get_move(mn_diff_handle handle, size_t index, mn_diff_move* move)
{
    try
    {
        const auto h = AsDiffHandle(handle);
        if (!h || !move || index >= h->result.moves.size()) return 0;
        const auto& value = h->result.moves[index];
        *move = mn_diff_move{
            value.originalStartLine,
            value.originalEndLineExclusive,
            value.modifiedStartLine,
            value.modifiedEndLineExclusive,
        };
        return 1;
    }
    catch (...) { return 0; }
}

size_t MN_CALL mn_model_length(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h ? h->model->Length() : 0;
}

size_t MN_CALL mn_model_line_count(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h ? h->model->GetLineCount() : 0;
}

uint64_t MN_CALL mn_model_version_id(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h ? h->model->VersionId() : 0;
}

int MN_CALL mn_model_can_undo(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h && h->model->CanUndo();
}

int MN_CALL mn_model_can_redo(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h && h->model->CanRedo();
}

int MN_CALL mn_model_undo(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h && h->model->Undo();
}

int MN_CALL mn_model_redo(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h && h->model->Redo();
}

int MN_CALL mn_model_begin_undo_transaction(mn_model_handle handle)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h) return 0;
        h->model->BeginUndoTransaction();
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_model_end_undo_transaction(mn_model_handle handle)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h) return 0;
        h->model->EndUndoTransaction();
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_model_rollback_undo_transaction(mn_model_handle handle)
{
    try
    {
        const auto h = AsHandle(handle);
        return h && h->model->RollbackUndoTransaction();
    }
    catch (...) { return 0; }
}

int MN_CALL mn_model_push_undo_stop(mn_model_handle handle)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h) return 0;
        h->model->PushUndoStop();
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_model_in_undo_transaction(mn_model_handle handle)
{
    const auto h = AsHandle(handle);
    return h && h->model->InUndoTransaction();
}

int MN_CALL mn_model_set_text(mn_model_handle handle, const mn_char16* text)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h)
        {
            return 0;
        }
        h->model->SetValue(ToUtf16(text));
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_model_copy_text(mn_model_handle handle, mn_char16* buffer, size_t buffer_chars)
{
    try
    {
        const auto h = AsHandle(handle);
        return h ? CopyUtf16(h->model->GetValue(), buffer, buffer_chars) : 0;
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_model_copy_text_range(mn_model_handle handle, size_t start, size_t length, mn_char16* buffer, size_t buffer_chars)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || start > h->model->Length())
        {
            return 0;
        }
        const auto safeLength = (std::min)(length, h->model->Length() - start);
        return CopyUtf16(h->model->GetValue(start, safeLength), buffer, buffer_chars);
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_model_copy_line(mn_model_handle handle, int32_t line_number, mn_char16* buffer, size_t buffer_chars)
{
    try
    {
        const auto h = AsHandle(handle);
        return h ? CopyUtf16(h->model->GetLineContent(line_number), buffer, buffer_chars) : 0;
    }
    catch (...)
    {
        return 0;
    }
}

int32_t MN_CALL mn_model_line_max_column(mn_model_handle handle, int32_t line_number)
{
    try
    {
        const auto h = AsHandle(handle);
        return h ? h->model->GetLineMaxColumn(line_number) : 0;
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_replace(mn_model_handle handle, size_t start, size_t end, const mn_char16* text)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || start > end || end > h->model->Length())
        {
            return 0;
        }
        h->model->ApplyOffsetEdits({OffsetEdit{start, end, ToUtf16(text)}}, true);
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_model_replace_all(
    mn_model_handle handle,
    const mn_char16* query,
    const mn_char16* replacement,
    int match_case,
    int whole_word,
    size_t limit)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h)
        {
            return 0;
        }
        return h->model->ReplaceAll(ToUtf16(query), ToUtf16(replacement), match_case != 0, whole_word != 0, limit);
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_set_language(mn_model_handle handle, const char* language_id)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h)
        {
            return 0;
        }
        h->model->SetLanguageId(language_id ? language_id : "plaintext");
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_model_copy_language(mn_model_handle handle, char* buffer, size_t buffer_bytes)
{
    try
    {
        const auto h = AsHandle(handle);
        return h ? CopyUtf8(h->model->LanguageId(), buffer, buffer_bytes) : 0;
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_get_position_at(mn_model_handle handle, size_t offset, int32_t* line_number, int32_t* column)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || !line_number || !column)
        {
            return 0;
        }
        const auto position = h->model->GetPositionAt(offset);
        *line_number = position.lineNumber;
        *column = position.column;
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_get_offset_at(mn_model_handle handle, int32_t line_number, int32_t column, size_t* offset)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || !offset)
        {
            return 0;
        }
        *offset = h->model->GetOffsetAt(Position{line_number, column});
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_get_eol(mn_model_handle handle)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h)
        {
            return -1;
        }
        return h->model->GetEOL() == EndOfLineSequence::CRLF ? 1 : 0;
    }
    catch (...)
    {
        return -1;
    }
}

int MN_CALL mn_model_set_eol(mn_model_handle handle, int eol)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || (eol != 0 && eol != 1))
        {
            return 0;
        }
        h->model->SetEOL(eol == 1 ? EndOfLineSequence::CRLF : EndOfLineSequence::LF, true);
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}


size_t MN_CALL mn_model_find_matches(
    mn_model_handle handle,
    const mn_char16* query,
    int match_case,
    int whole_word,
    size_t limit,
    mn_range* ranges,
    size_t range_capacity)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h)
        {
            return 0;
        }
        const auto matches = h->model->FindMatches(ToUtf16(query), match_case != 0, whole_word != 0, limit);
        if (ranges && range_capacity)
        {
            const auto count = (std::min)(matches.size(), range_capacity);
            for (std::size_t i = 0; i < count; ++i)
            {
                ranges[i] = mn_range{
                    matches[i].range.startLineNumber,
                    matches[i].range.startColumn,
                    matches[i].range.endLineNumber,
                    matches[i].range.endColumn,
                };
            }
        }
        return matches.size();
    }
    catch (...)
    {
        return 0;
    }
}

int MN_CALL mn_model_find_matching_bracket(
    mn_model_handle handle,
    int32_t line_number,
    int32_t column,
    mn_bracket_match* result)
{
    try
    {
        const auto h = AsHandle(handle);
        if (!h || !result)
        {
            return -1;
        }
        const auto match = h->model->FindMatchingBracket(Position{line_number, column});
        if (!match)
        {
            return 0;
        }
        *result = mn_bracket_match{
            match->bracket.lineNumber,
            match->bracket.column,
            match->match.lineNumber,
            match->match.column,
        };
        return 1;
    }
    catch (...)
    {
        return -1;
    }
}

mn_view_handle MN_CALL mn_view_create(mn_model_handle model_handle)
{
    try
    {
        const auto model = AsHandle(model_handle);
        if (!model || !model->model)
        {
            return nullptr;
        }
        auto result = std::make_unique<ViewHandle>();
        result->model = model->model;
        result->view = std::make_unique<ViewModel>(result->model);
        return result.release();
    }
    catch (...)
    {
        return nullptr;
    }
}

void MN_CALL mn_view_destroy(mn_view_handle handle)
{
    delete AsViewHandle(handle);
}

int MN_CALL mn_view_set_wrap_column(mn_view_handle handle, int32_t wrap_column)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h) return 0;
        h->view->SetWrapColumn(wrap_column);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_set_wrap_width(mn_view_handle handle, double wrap_width)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h) return 0;
        h->view->SetWrapWidth(wrap_width);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_set_line_height(mn_view_handle handle, double line_height)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h) return 0;
        h->view->SetLineHeight(line_height);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_set_measure_callback(mn_view_handle handle, mn_measure_text_callback callback, void* context)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h) return 0;
        if (!callback)
        {
            h->callbackMeasurer.reset();
            h->view->SetTextMeasurer(nullptr);
            return 1;
        }
        h->callbackMeasurer = std::make_shared<CallbackTextMeasurer>(callback, context);
        h->view->SetTextMeasurer(h->callbackMeasurer);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_rebuild(mn_view_handle handle)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h) return 0;
        h->view->Rebuild();
        return 1;
    }
    catch (...) { return 0; }
}

int32_t MN_CALL mn_view_line_count(mn_view_handle handle)
{
    const auto h = AsViewHandle(handle);
    return h ? h->view->ViewLineCount() : 0;
}

size_t MN_CALL mn_view_copy_line(mn_view_handle handle, int32_t view_line_number, mn_char16* buffer, size_t buffer_chars)
{
    try
    {
        const auto h = AsViewHandle(handle);
        return h ? CopyUtf16(h->view->GetViewLine(view_line_number).text, buffer, buffer_chars) : 0;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_get_line_info(mn_view_handle handle, int32_t view_line_number, mn_view_line_info* info)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || !info) return 0;
        const auto& line = h->view->GetViewLine(view_line_number);
        *info = mn_view_line_info{line.viewLineNumber, line.modelLineNumber, line.startColumn, line.endColumn, line.wrappedIndex};
        return 1;
    }
    catch (...) { return 0; }
}

double MN_CALL mn_view_get_vertical_offset(mn_view_handle handle, int32_t view_line_number)
{
    const auto h = AsViewHandle(handle);
    return h ? h->view->GetVerticalOffsetForViewLine(view_line_number) : 0.0;
}

double MN_CALL mn_view_total_height(mn_view_handle handle)
{
    const auto h = AsViewHandle(handle);
    return h ? h->view->TotalHeight() : 0.0;
}

int32_t MN_CALL mn_view_line_at_vertical_offset(mn_view_handle handle, double offset)
{
    const auto h = AsViewHandle(handle);
    return h ? h->view->GetViewLineAtVerticalOffset(offset) : 0;
}

int MN_CALL mn_view_model_to_view(mn_view_handle handle, int32_t model_line, int32_t model_column, int32_t* view_line, int32_t* view_column)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || !view_line || !view_column) return 0;
        const auto position = h->view->ConvertModelPositionToViewPosition(Position{model_line, model_column});
        *view_line = position.lineNumber;
        *view_column = position.column;
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_view_to_model(mn_view_handle handle, int32_t view_line, int32_t view_column, int32_t* model_line, int32_t* model_column)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || !model_line || !model_column) return 0;
        const auto position = h->view->ConvertViewPositionToModelPosition({view_line, view_column});
        *model_line = position.lineNumber;
        *model_column = position.column;
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_set_hidden_areas(mn_view_handle handle, const mn_hidden_area* areas, size_t count)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || (count > 0 && !areas)) return 0;
        std::vector<HiddenArea> values;
        values.reserve(count);
        for (size_t i = 0; i < count; ++i)
        {
            values.push_back(HiddenArea{areas[i].start_line_number, areas[i].end_line_number});
        }
        h->view->SetHiddenAreas(std::move(values));
        return 1;
    }
    catch (...) { return 0; }
}

uint64_t MN_CALL mn_view_add_view_zone(mn_view_handle handle, int32_t after_line_number, double height)
{
    try
    {
        const auto h = AsViewHandle(handle);
        return h ? h->view->AddViewZone(after_line_number, height) : 0;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_remove_view_zone(mn_view_handle handle, uint64_t id)
{
    try
    {
        const auto h = AsViewHandle(handle);
        return h && h->view->RemoveViewZone(id);
    }
    catch (...) { return 0; }
}

void MN_CALL mn_view_clear_view_zones(mn_view_handle handle)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (h) h->view->ClearViewZones();
    }
    catch (...) {}
}

uint64_t MN_CALL mn_view_add_injected_text(mn_view_handle handle, int32_t line_number, int32_t column, const mn_char16* text, int affinity, int32_t order)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || affinity < 0 || affinity > 1) return 0;
        return h->view->AddInjectedText(Position{line_number, column}, ToUtf16(text), static_cast<InjectedTextAffinity>(affinity), order);
    }
    catch (...) { return 0; }
}

int MN_CALL mn_view_remove_injected_text(mn_view_handle handle, uint64_t id)
{
    try
    {
        const auto h = AsViewHandle(handle);
        return h && h->view->RemoveInjectedText(id);
    }
    catch (...) { return 0; }
}

void MN_CALL mn_view_clear_injected_text(mn_view_handle handle)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (h) h->view->ClearInjectedText();
    }
    catch (...) {}
}

double MN_CALL mn_view_scroll_top_to_reveal(mn_view_handle handle, mn_range range, double viewport_height, double current_scroll_top, int strategy, double padding)
{
    try
    {
        const auto h = AsViewHandle(handle);
        if (!h || strategy < 0 || strategy > 3) return 0.0;
        const Range value{range.start_line, range.start_column, range.end_line, range.end_column};
        return h->view->GetScrollTopToRevealRange(value, viewport_height, current_scroll_top, static_cast<RevealStrategy>(strategy), padding);
    }
    catch (...) { return 0.0; }
}



mn_keybinding_handle MN_CALL mn_keybinding_create()
{
    try
    {
        return new KeybindingHandle();
    }
    catch (...) { return nullptr; }
}

void MN_CALL mn_keybinding_destroy(mn_keybinding_handle handle)
{
    delete AsKeybindingHandle(handle);
}

int MN_CALL mn_keybinding_set_context_bool(mn_keybinding_handle handle, const char* key, int value)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key || *key == '\0') return 0;
        h->context.Set(key, value != 0);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_keybinding_set_context_number(mn_keybinding_handle handle, const char* key, double value)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key || *key == '\0') return 0;
        h->context.Set(key, value);
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_keybinding_set_context_string(mn_keybinding_handle handle, const char* key, const char* value)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key || *key == '\0') return 0;
        h->context.Set(key, std::string(value ? value : ""));
        return 1;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_keybinding_reset_context(mn_keybinding_handle handle, const char* key)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key) return 0;
        return h->context.Reset(key) ? 1 : 0;
    }
    catch (...) { return 0; }
}

int MN_CALL mn_keybinding_context_matches(mn_keybinding_handle handle, const char* expression)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h) return -1;
        return h->context.Evaluate(expression ? expression : "") ? 1 : 0;
    }
    catch (...) { return -1; }
}

uint64_t MN_CALL mn_keybinding_register(
    mn_keybinding_handle handle,
    const char* command_id,
    const char* chord,
    const char* when_clause,
    int32_t weight,
    int prevent_default)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !command_id || !*command_id || !chord || !*chord) return 0;
        KeybindingRule rule{};
        rule.commandId = command_id;
        rule.chord = KeybindingResolver::ParseChord(chord);
        rule.when = when_clause ? when_clause : "";
        rule.weight = weight;
        rule.preventDefault = prevent_default != 0;
        return h->resolver.Register(std::move(rule));
    }
    catch (...) { return 0; }
}

int MN_CALL mn_keybinding_unregister(mn_keybinding_handle handle, uint64_t token)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        return h && h->resolver.Unregister(token);
    }
    catch (...) { return 0; }
}

void MN_CALL mn_keybinding_reset_chord(mn_keybinding_handle handle)
{
    const auto h = AsKeybindingHandle(handle);
    if (h)
    {
        h->resolver.ResetChord();
        h->resolvedCommand.clear();
        h->resolvedChord.clear();
    }
}

int MN_CALL mn_keybinding_in_chord_mode(mn_keybinding_handle handle)
{
    const auto h = AsKeybindingHandle(handle);
    return h && h->resolver.InChordMode();
}

int MN_CALL mn_keybinding_resolve(mn_keybinding_handle handle, const char* key, uint8_t modifiers, int* prevent_default)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key || !*key) return -1;
        const auto result = h->resolver.Resolve(KeyStroke{key, modifiers}, h->context);
        h->resolvedCommand = result.commandId;
        h->resolvedChord = result.chordLabel;
        if (prevent_default) *prevent_default = result.preventDefault ? 1 : 0;
        switch (result.kind)
        {
        case KeybindingResultKind::NoMatch: return 0;
        case KeybindingResultKind::Command: return 1;
        case KeybindingResultKind::MoreChordsNeeded: return 2;
        case KeybindingResultKind::ChordCancelled: return 3;
        }
        return -1;
    }
    catch (...) { return -1; }
}

int MN_CALL mn_keybinding_resolve_snapshot(
    mn_keybinding_handle handle,
    const char* key,
    uint8_t modifiers,
    mn_keybinding_resolution* result)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h || !key || !*key || !result || result->struct_size < sizeof(uint32_t)) return 0;

        const auto resolved = h->resolver.Resolve(KeyStroke{key, modifiers}, h->context);
        h->resolvedCommand = resolved.commandId;
        h->resolvedChord = resolved.chordLabel;

        const mn_keybinding_resolution current{
            static_cast<uint32_t>(sizeof(mn_keybinding_resolution)),
            static_cast<int32_t>(resolved.kind),
            resolved.preventDefault ? 1 : 0,
            h->resolvedCommand.c_str(),
            h->resolvedChord.c_str(),
        };
        const auto bytes = std::min<std::size_t>(result->struct_size, sizeof(current));
        std::memcpy(result, &current, bytes);
        return 1;
    }
    catch (...)
    {
        return 0;
    }
}

size_t MN_CALL mn_keybinding_copy_resolved_command(mn_keybinding_handle handle, char* buffer, size_t buffer_bytes)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        return h ? CopyUtf8(h->resolvedCommand, buffer, buffer_bytes) : 0;
    }
    catch (...) { return 0; }
}

size_t MN_CALL mn_keybinding_copy_pending_chord(mn_keybinding_handle handle, char* buffer, size_t buffer_bytes)
{
    try
    {
        const auto h = AsKeybindingHandle(handle);
        if (!h) return 0;
        h->resolvedChord = h->resolver.PendingChordLabel();
        return CopyUtf8(h->resolvedChord, buffer, buffer_bytes);
    }
    catch (...) { return 0; }
}
