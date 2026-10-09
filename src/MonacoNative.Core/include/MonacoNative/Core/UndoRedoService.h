#pragma once

#include "TextTypes.h"

#include <cstddef>
#include <vector>

namespace MonacoNative::Core
{
    struct EditBatch final
    {
        std::vector<OffsetEdit> forward{};
        std::vector<OffsetEdit> inverse{};
    };

    // UTF-16 offsets, not Position values: each snapshot belongs to a different
    // document revision. Anchor and active are kept separately for reversed
    // selections, and there may be more than one caret.
    struct UndoSelection final
    {
        std::size_t anchor{};
        std::size_t active{};

        friend bool operator==(const UndoSelection&, const UndoSelection&) = default;
    };

    struct UndoRedoElement final
    {
        std::vector<EditBatch> steps{};
        std::vector<UndoSelection> beforeSelections{};
        std::vector<UndoSelection> afterSelections{};
        bool hasExplicitBeforeSelections{};
    };

    class UndoRedoService final
    {
    public:
        void Clear();
        void Push(EditBatch batch);
        [[nodiscard]] bool CanUndo() const noexcept;
        [[nodiscard]] bool CanRedo() const noexcept;
        [[nodiscard]] UndoRedoElement PopUndo();
        [[nodiscard]] UndoRedoElement PopRedo();
        void PushRedo(UndoRedoElement element);
        void PushUndoFromRedo(UndoRedoElement element);
        // The first edit of a transaction supplies "before"; the last edit
        // (or the editor after it moves the caret) supplies "after".
        bool SetLatestSelections(std::vector<UndoSelection> before, std::vector<UndoSelection> after);
        bool SetLatestAfterSelections(std::vector<UndoSelection> after);

        void BeginTransaction();
        void EndTransaction();
        [[nodiscard]] UndoRedoElement RollbackTransaction();
        void PushUndoStop();
        [[nodiscard]] bool InTransaction() const noexcept;
        [[nodiscard]] std::size_t TransactionDepth() const noexcept;

    private:
        void CommitPending();

        std::vector<UndoRedoElement> undoStack_{};
        std::vector<UndoRedoElement> redoStack_{};
        UndoRedoElement pending_{};
        std::size_t transactionDepth_{};
        std::size_t transactionStartUndoSize_{};
        std::vector<UndoRedoElement> transactionRedoBackup_{};
        bool redoClearedForPending_{};
    };
}
