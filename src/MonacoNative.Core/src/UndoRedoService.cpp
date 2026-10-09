#include "MonacoNative/Core/UndoRedoService.h"

#include <stdexcept>
#include <utility>

namespace MonacoNative::Core
{
    namespace
    {
        // Useful for headless/model clients that never provide editor cursor
        // metadata. Interactive editor operations overwrite these defaults.
        std::vector<UndoSelection> DefaultBeforeSelections(const EditBatch& batch)
        {
            if (batch.forward.empty()) return {};
            const auto& first = batch.forward.front();
            if (first.start != first.end && !first.text.empty())
            {
                return {{first.start, first.end}};
            }
            const auto offset = first.text.empty() ? first.end : first.start;
            return {{offset, offset}};
        }

        std::vector<UndoSelection> DefaultAfterSelections(const EditBatch& batch)
        {
            if (batch.inverse.empty()) return {};
            const auto offset = batch.inverse.front().end;
            return {{offset, offset}};
        }
    }

    void UndoRedoService::Clear()
    {
        undoStack_.clear();
        redoStack_.clear();
        pending_ = {};
        transactionDepth_ = 0;
        transactionStartUndoSize_ = 0;
        transactionRedoBackup_.clear();
        redoClearedForPending_ = false;
    }

    void UndoRedoService::Push(EditBatch batch)
    {
        if (batch.forward.empty() && batch.inverse.empty())
        {
            return;
        }

        if (transactionDepth_ != 0)
        {
            if (!redoClearedForPending_)
            {
                redoStack_.clear();
                redoClearedForPending_ = true;
            }
            if (pending_.steps.empty())
            {
                pending_.beforeSelections = DefaultBeforeSelections(batch);
                pending_.hasExplicitBeforeSelections = false;
            }
            pending_.afterSelections = DefaultAfterSelections(batch);
            pending_.steps.push_back(std::move(batch));
            return;
        }

        UndoRedoElement element{};
        element.beforeSelections = DefaultBeforeSelections(batch);
        element.afterSelections = DefaultAfterSelections(batch);
        element.steps.push_back(std::move(batch));
        undoStack_.push_back(std::move(element));
        redoStack_.clear();
    }

    bool UndoRedoService::CanUndo() const noexcept
    {
        return !undoStack_.empty();
    }

    bool UndoRedoService::CanRedo() const noexcept
    {
        return !redoStack_.empty();
    }

    UndoRedoElement UndoRedoService::PopUndo()
    {
        if (transactionDepth_ != 0)
        {
            throw std::logic_error("Cannot undo while an undo transaction is open.");
        }
        if (undoStack_.empty())
        {
            throw std::logic_error("Undo stack is empty.");
        }
        auto element = std::move(undoStack_.back());
        undoStack_.pop_back();
        return element;
    }

    UndoRedoElement UndoRedoService::PopRedo()
    {
        if (transactionDepth_ != 0)
        {
            throw std::logic_error("Cannot redo while an undo transaction is open.");
        }
        if (redoStack_.empty())
        {
            throw std::logic_error("Redo stack is empty.");
        }
        auto element = std::move(redoStack_.back());
        redoStack_.pop_back();
        return element;
    }

    void UndoRedoService::PushRedo(UndoRedoElement element)
    {
        redoStack_.push_back(std::move(element));
    }

    void UndoRedoService::PushUndoFromRedo(UndoRedoElement element)
    {
        undoStack_.push_back(std::move(element));
    }

    bool UndoRedoService::SetLatestSelections(
        std::vector<UndoSelection> before, std::vector<UndoSelection> after)
    {
        UndoRedoElement* element = nullptr;
        if (transactionDepth_ != 0)
        {
            if (!pending_.steps.empty()) element = &pending_;
        }
        else if (!undoStack_.empty())
        {
            element = &undoStack_.back();
        }
        if (!element || before.empty() || after.empty()) return false;
        if (!element->hasExplicitBeforeSelections)
        {
            element->beforeSelections = std::move(before);
            element->hasExplicitBeforeSelections = true;
        }
        element->afterSelections = std::move(after);
        return true;
    }

    bool UndoRedoService::SetLatestAfterSelections(std::vector<UndoSelection> after)
    {
        UndoRedoElement* element = nullptr;
        if (transactionDepth_ != 0)
        {
            if (!pending_.steps.empty()) element = &pending_;
        }
        else if (!undoStack_.empty())
        {
            element = &undoStack_.back();
        }
        if (!element || after.empty()) return false;
        element->afterSelections = std::move(after);
        return true;
    }

    void UndoRedoService::BeginTransaction()
    {
        if (transactionDepth_ == 0)
        {
            transactionStartUndoSize_ = undoStack_.size();
            transactionRedoBackup_ = redoStack_;
            pending_ = {};
            redoClearedForPending_ = false;
        }
        ++transactionDepth_;
    }

    void UndoRedoService::EndTransaction()
    {
        if (transactionDepth_ == 0)
        {
            throw std::logic_error("No undo transaction is open.");
        }
        --transactionDepth_;
        if (transactionDepth_ == 0)
        {
            CommitPending();
            transactionStartUndoSize_ = undoStack_.size();
            transactionRedoBackup_.clear();
            redoClearedForPending_ = false;
        }
    }

    UndoRedoElement UndoRedoService::RollbackTransaction()
    {
        if (transactionDepth_ == 0)
        {
            throw std::logic_error("No undo transaction is open.");
        }

        UndoRedoElement result{};
        for (std::size_t i = transactionStartUndoSize_; i < undoStack_.size(); ++i)
        {
            auto& element = undoStack_[i];
            for (auto& step : element.steps)
            {
                result.steps.push_back(std::move(step));
            }
        }
        for (auto& step : pending_.steps)
        {
            result.steps.push_back(std::move(step));
        }

        undoStack_.resize(transactionStartUndoSize_);
        redoStack_ = std::move(transactionRedoBackup_);
        transactionRedoBackup_.clear();
        pending_ = {};
        transactionDepth_ = 0;
        transactionStartUndoSize_ = undoStack_.size();
        redoClearedForPending_ = false;
        return result;
    }

    void UndoRedoService::PushUndoStop()
    {
        if (transactionDepth_ == 0)
        {
            return;
        }
        CommitPending();
        redoClearedForPending_ = false;
    }

    bool UndoRedoService::InTransaction() const noexcept
    {
        return transactionDepth_ != 0;
    }

    std::size_t UndoRedoService::TransactionDepth() const noexcept
    {
        return transactionDepth_;
    }

    void UndoRedoService::CommitPending()
    {
        if (pending_.steps.empty())
        {
            return;
        }
        undoStack_.push_back(std::move(pending_));
        pending_ = {};
    }
}
