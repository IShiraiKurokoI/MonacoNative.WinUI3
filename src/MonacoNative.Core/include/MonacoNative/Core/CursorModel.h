#pragma once

#include "Event.h"
#include "TextModel.h"
#include "TextTypes.h"

#include <cstddef>
#include <memory>
#include <optional>
#include <vector>

namespace MonacoNative::Core
{
    class CursorModel final
    {
    public:
        explicit CursorModel(std::shared_ptr<TextModel> model);

        [[nodiscard]] const std::vector<Selection>& Selections() const noexcept;
        void SetSelections(std::vector<Selection> selections);
        void SetPrimarySelection(Selection selection);
        [[nodiscard]] Selection PrimarySelection() const;
        [[nodiscard]] EventSource<std::vector<Selection>>& OnDidChangeSelection() noexcept;

        void MoveLeft(bool extendSelection = false);
        void MoveRight(bool extendSelection = false);
        void MoveWordLeft(bool extendSelection = false);
        void MoveWordRight(bool extendSelection = false);
        void MoveUp(bool extendSelection = false);
        void MoveDown(bool extendSelection = false);
        void MoveHome(bool extendSelection = false);
        void MoveEnd(bool extendSelection = false);
        void InsertText(std::u16string_view text);
        void Backspace();
        void DeleteForward();
        void DeleteWordLeft();
        void DeleteWordRight();

    private:
        void MovePrimaryTo(Position position, bool extendSelection, bool preservePreferredColumn = false);
        [[nodiscard]] Range SelectionRange(const Selection& selection) const;
        void DeleteSelections(bool backward, bool byWord);
        void NormalizeSelections();
        void EmitSelectionChangedIfNeeded(const std::vector<Selection>& previous);

        std::shared_ptr<TextModel> model_{};
        std::vector<Selection> selections_{{}};
        std::optional<std::int32_t> preferredColumn_{};
        EventSource<std::vector<Selection>> didChangeSelection_{};
    };
}
