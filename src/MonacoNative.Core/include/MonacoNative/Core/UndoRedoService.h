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

    struct UndoRedoElement final
    {
        std::vector<EditBatch> steps{};
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
