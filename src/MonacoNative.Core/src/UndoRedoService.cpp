#include "MonacoNative/Core/UndoRedoService.h"

#include <stdexcept>
#include <utility>

namespace MonacoNative::Core
{
    void UndoRedoService::Clear()
    {
        undoStack_.clear();
        redoStack_.clear();
        pending_.steps.clear();
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
            pending_.steps.push_back(std::move(batch));
            return;
        }

        undoStack_.push_back(UndoRedoElement{{std::move(batch)}});
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

    void UndoRedoService::BeginTransaction()
    {
        if (transactionDepth_ == 0)
        {
            transactionStartUndoSize_ = undoStack_.size();
            transactionRedoBackup_ = redoStack_;
            pending_.steps.clear();
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
