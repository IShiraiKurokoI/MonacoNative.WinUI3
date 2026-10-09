#pragma once

#include "Decorations.h"
#include "Event.h"
#include "PieceTreeTextBuffer.h"
#include "TextTypes.h"
#include "UndoRedoService.h"

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace MonacoNative::Core
{
    enum class EndOfLineSequence : std::uint8_t
    {
        LF,
        CRLF,
    };

    struct FindMatch final
    {
        Range range{};
        std::u16string text{};
    };

    struct BracketMatch final
    {
        Position bracket{};
        Position match{};
    };

    struct TextSnapshot final
    {
        std::uint64_t versionId{};
        std::string languageId{};
        std::u16string text{};
    };

    class TextModel final
    {
    public:
        TextModel();
        explicit TextModel(std::u16string text, std::string languageId = "plaintext");

        [[nodiscard]] std::uint64_t VersionId() const noexcept;
        [[nodiscard]] std::string_view LanguageId() const noexcept;
        void SetLanguageId(std::string languageId);

        [[nodiscard]] std::u16string GetValue() const;
        [[nodiscard]] std::u16string GetValue(std::size_t start, std::size_t length) const;
        [[nodiscard]] std::u16string GetValueInRange(Range range) const;
        [[nodiscard]] std::u16string GetLineContent(std::int32_t lineNumber) const;
        [[nodiscard]] std::int32_t GetLineMaxColumn(std::int32_t lineNumber) const;
        [[nodiscard]] std::size_t GetLineCount() const;
        [[nodiscard]] Position GetPositionAt(std::size_t offset) const;
        [[nodiscard]] std::size_t GetOffsetAt(Position position) const;
        [[nodiscard]] std::size_t Length() const noexcept;
        [[nodiscard]] TextSnapshot CreateSnapshot() const;

        [[nodiscard]] EndOfLineSequence GetEOL() const;
        void SetEOL(EndOfLineSequence eol, bool recordUndo = true);
        [[nodiscard]] std::u16string GetLineIndentation(std::int32_t lineNumber) const;
        [[nodiscard]] std::u16string NormalizeIndentation(std::u16string_view indentation, std::int32_t tabSize = 4, bool insertSpaces = true) const;
        [[nodiscard]] std::vector<FindMatch> FindMatches(std::u16string_view query, bool matchCase = true, bool wholeWord = false, std::size_t limit = 1000) const;
        std::size_t ReplaceAll(std::u16string_view query, std::u16string_view replacement, bool matchCase = true, bool wholeWord = false, std::size_t limit = 1000);
        [[nodiscard]] std::optional<BracketMatch> FindMatchingBracket(Position position) const;

        [[nodiscard]] DecorationCollection& Decorations() noexcept;
        [[nodiscard]] const DecorationCollection& Decorations() const noexcept;

        void SetValue(std::u16string text);
        void ApplyEdits(std::vector<TextEdit> edits, bool recordUndo = true);
        void ApplyOffsetEdits(std::vector<OffsetEdit> edits, bool recordUndo = true);

        [[nodiscard]] bool CanUndo() const noexcept;
        [[nodiscard]] bool CanRedo() const noexcept;
        bool Undo();
        bool Redo();
        bool SetLatestUndoSelections(std::vector<UndoSelection> before, std::vector<UndoSelection> after);
        bool SetLatestUndoAfterSelections(std::vector<UndoSelection> after);
        [[nodiscard]] const std::vector<UndoSelection>& LastRestoredSelections() const noexcept;

        void BeginUndoTransaction();
        void EndUndoTransaction();
        bool RollbackUndoTransaction();
        void PushUndoStop();
        [[nodiscard]] bool InUndoTransaction() const noexcept;

        [[nodiscard]] EventSource<ModelContentChangedEvent>& OnDidChangeContent() noexcept;

    private:
        void ApplyOffsetEditsInternal(std::vector<OffsetEdit> edits, bool recordUndo, bool isUndoing, bool isRedoing);
        void TransformDecorations(const std::vector<OffsetEdit>& edits, const std::vector<std::pair<DecorationId, std::pair<std::size_t, std::size_t>>>& oldDecorationOffsets);

        PieceTreeTextBuffer buffer_{};
        UndoRedoService undoRedo_{};
        std::vector<UndoSelection> lastRestoredSelections_{};
        DecorationCollection decorations_{};
        std::uint64_t versionId_{1};
        std::string languageId_{"plaintext"};
        EventSource<ModelContentChangedEvent> didChangeContent_{};
    };
}
