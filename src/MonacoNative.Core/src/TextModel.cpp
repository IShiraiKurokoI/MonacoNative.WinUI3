#include "MonacoNative/Core/TextModel.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <limits>
#include <stdexcept>
#include <utility>

namespace MonacoNative::Core
{
    namespace
    {
        bool IsWordCharacter(char16_t value) noexcept
        {
            if (value >= 0x80)
            {
                return true;
            }
            const auto ch = static_cast<unsigned char>(value);
            return std::isalnum(ch) != 0 || value == u'_';
        }

        char16_t FoldAscii(char16_t value) noexcept
        {
            if (value >= u'A' && value <= u'Z')
            {
                return static_cast<char16_t>(value + (u'a' - u'A'));
            }
            return value;
        }

        bool EqualAt(std::u16string_view text, std::size_t offset, std::u16string_view query, bool matchCase) noexcept
        {
            if (offset + query.size() > text.size())
            {
                return false;
            }
            for (std::size_t i = 0; i < query.size(); ++i)
            {
                const auto a = text[offset + i];
                const auto b = query[i];
                if (matchCase ? (a != b) : (FoldAscii(a) != FoldAscii(b)))
                {
                    return false;
                }
            }
            return true;
        }

        bool IsOpenBracket(char16_t value) noexcept
        {
            return value == u'(' || value == u'[' || value == u'{';
        }

        bool IsCloseBracket(char16_t value) noexcept
        {
            return value == u')' || value == u']' || value == u'}';
        }

        char16_t MatchingBracket(char16_t value) noexcept
        {
            switch (value)
            {
            case u'(': return u')';
            case u')': return u'(';
            case u'[': return u']';
            case u']': return u'[';
            case u'{': return u'}';
            case u'}': return u'{';
            default: return 0;
            }
        }

        bool GrowsAtStart(DecorationStickiness stickiness) noexcept
        {
            return stickiness == DecorationStickiness::AlwaysGrowsWhenTypingAtEdges ||
                stickiness == DecorationStickiness::GrowsOnlyWhenTypingBefore;
        }

        bool GrowsAtEnd(DecorationStickiness stickiness) noexcept
        {
            return stickiness == DecorationStickiness::AlwaysGrowsWhenTypingAtEdges ||
                stickiness == DecorationStickiness::GrowsOnlyWhenTypingAfter;
        }

        std::size_t TransformBoundary(
            std::size_t offset,
            bool isStart,
            DecorationStickiness stickiness,
            const std::vector<OffsetEdit>& edits)
        {
            std::ptrdiff_t delta = 0;
            for (const auto& edit : edits)
            {
                const auto inserted = edit.text.size();
                const auto removed = edit.end - edit.start;
                const auto transformedStart = static_cast<std::size_t>(static_cast<std::ptrdiff_t>(edit.start) + delta);

                if (offset < edit.start)
                {
                    break;
                }

                if (edit.start == edit.end && offset == edit.start)
                {
                    const auto grows = isStart ? GrowsAtStart(stickiness) : GrowsAtEnd(stickiness);
                    if (isStart)
                    {
                        return grows ? transformedStart : transformedStart + inserted;
                    }
                    return grows ? transformedStart + inserted : transformedStart;
                }

                if (offset > edit.end || (removed > 0 && offset == edit.end))
                {
                    delta += static_cast<std::ptrdiff_t>(inserted) - static_cast<std::ptrdiff_t>(removed);
                    continue;
                }

                if (offset == edit.start)
                {
                    return transformedStart;
                }

                if (offset < edit.end)
                {
                    return isStart ? transformedStart : transformedStart + inserted;
                }
            }

            const auto result = static_cast<std::ptrdiff_t>(offset) + delta;
            return result < 0 ? 0U : static_cast<std::size_t>(result);
        }
    }

    TextModel::TextModel() = default;

    TextModel::TextModel(std::u16string text, std::string languageId)
        : buffer_(std::move(text)), languageId_(std::move(languageId))
    {
    }

    std::uint64_t TextModel::VersionId() const noexcept
    {
        return versionId_;
    }

    std::string_view TextModel::LanguageId() const noexcept
    {
        return languageId_;
    }

    void TextModel::SetLanguageId(std::string languageId)
    {
        languageId_ = std::move(languageId);
    }

    std::u16string TextModel::GetValue() const
    {
        return buffer_.GetText();
    }

    std::u16string TextModel::GetValue(std::size_t start, std::size_t length) const
    {
        return buffer_.GetText(start, length);
    }

    std::u16string TextModel::GetValueInRange(Range range) const
    {
        const auto start = buffer_.GetOffsetAt(Position{range.startLineNumber, range.startColumn});
        const auto end = buffer_.GetOffsetAt(Position{range.endLineNumber, range.endColumn});
        const auto first = std::min(start, end);
        const auto last = std::max(start, end);
        return buffer_.GetText(first, last - first);
    }

    std::u16string TextModel::GetLineContent(std::int32_t lineNumber) const
    {
        return buffer_.GetLineContent(lineNumber);
    }

    std::int32_t TextModel::GetLineMaxColumn(std::int32_t lineNumber) const
    {
        return static_cast<std::int32_t>(buffer_.GetLineLength(lineNumber) + 1);
    }

    std::size_t TextModel::GetLineCount() const
    {
        return buffer_.LineCount();
    }

    Position TextModel::GetPositionAt(std::size_t offset) const
    {
        return buffer_.GetPositionAt(offset);
    }

    std::size_t TextModel::GetOffsetAt(Position position) const
    {
        return buffer_.GetOffsetAt(position);
    }

    std::size_t TextModel::Length() const noexcept
    {
        return buffer_.Length();
    }

    TextSnapshot TextModel::CreateSnapshot() const
    {
        return TextSnapshot{versionId_, languageId_, buffer_.GetText()};
    }

    EndOfLineSequence TextModel::GetEOL() const
    {
        const auto text = buffer_.GetText();
        std::size_t crlf = 0;
        std::size_t lf = 0;
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            if (text[i] != u'\n')
            {
                continue;
            }
            if (i > 0 && text[i - 1] == u'\r')
            {
                ++crlf;
            }
            else
            {
                ++lf;
            }
        }
        return crlf > lf ? EndOfLineSequence::CRLF : EndOfLineSequence::LF;
    }

    void TextModel::SetEOL(EndOfLineSequence eol, bool recordUndo)
    {
        const auto old = buffer_.GetText();
        std::u16string normalized;
        normalized.reserve(old.size());
        for (std::size_t i = 0; i < old.size(); ++i)
        {
            if (old[i] == u'\r')
            {
                if (i + 1 < old.size() && old[i + 1] == u'\n')
                {
                    continue;
                }
                normalized.push_back(u'\n');
            }
            else
            {
                normalized.push_back(old[i]);
            }
        }

        std::u16string replacement;
        if (eol == EndOfLineSequence::LF)
        {
            replacement = std::move(normalized);
        }
        else
        {
            replacement.reserve(normalized.size() + static_cast<std::size_t>(std::count(normalized.begin(), normalized.end(), u'\n')));
            for (const auto ch : normalized)
            {
                if (ch == u'\n')
                {
                    replacement.push_back(u'\r');
                }
                replacement.push_back(ch);
            }
        }

        if (replacement != old)
        {
            ApplyOffsetEdits({OffsetEdit{0, old.size(), std::move(replacement)}}, recordUndo);
        }
    }

    std::u16string TextModel::GetLineIndentation(std::int32_t lineNumber) const
    {
        const auto line = GetLineContent(lineNumber);
        std::size_t count = 0;
        while (count < line.size() && (line[count] == u' ' || line[count] == u'\t'))
        {
            ++count;
        }
        return line.substr(0, count);
    }

    std::u16string TextModel::NormalizeIndentation(std::u16string_view indentation, std::int32_t tabSize, bool insertSpaces) const
    {
        tabSize = std::max(1, tabSize);
        std::int32_t columns = 0;
        for (const auto ch : indentation)
        {
            if (ch == u'\t')
            {
                columns += tabSize - (columns % tabSize);
            }
            else if (ch == u' ')
            {
                ++columns;
            }
            else
            {
                break;
            }
        }

        if (insertSpaces)
        {
            return std::u16string(static_cast<std::size_t>(columns), u' ');
        }

        std::u16string result(static_cast<std::size_t>(columns / tabSize), u'\t');
        result.append(static_cast<std::size_t>(columns % tabSize), u' ');
        return result;
    }

    std::vector<FindMatch> TextModel::FindMatches(std::u16string_view query, bool matchCase, bool wholeWord, std::size_t limit) const
    {
        std::vector<FindMatch> matches;
        if (query.empty() || limit == 0)
        {
            return matches;
        }

        const auto text = buffer_.GetText();
        for (std::size_t i = 0; i + query.size() <= text.size() && matches.size() < limit; )
        {
            if (!EqualAt(text, i, query, matchCase))
            {
                ++i;
                continue;
            }

            if (wholeWord)
            {
                const auto leftWord = i > 0 && IsWordCharacter(text[i - 1]);
                const auto rightIndex = i + query.size();
                const auto rightWord = rightIndex < text.size() && IsWordCharacter(text[rightIndex]);
                const auto queryStartsWord = IsWordCharacter(query.front());
                const auto queryEndsWord = IsWordCharacter(query.back());
                if ((queryStartsWord && leftWord) || (queryEndsWord && rightWord))
                {
                    ++i;
                    continue;
                }
            }

            const auto start = buffer_.GetPositionAt(i);
            const auto end = buffer_.GetPositionAt(i + query.size());
            matches.push_back(FindMatch{
                Range{start.lineNumber, start.column, end.lineNumber, end.column},
                text.substr(i, query.size()),
            });
            i += std::max<std::size_t>(1, query.size());
        }
        return matches;
    }

    std::size_t TextModel::ReplaceAll(
        std::u16string_view query,
        std::u16string_view replacement,
        bool matchCase,
        bool wholeWord,
        std::size_t limit)
    {
        const auto matches = FindMatches(query, matchCase, wholeWord, limit);
        if (matches.empty())
        {
            return 0;
        }

        std::vector<OffsetEdit> edits;
        edits.reserve(matches.size());
        for (const auto& match : matches)
        {
            const auto start = buffer_.GetOffsetAt(Position{match.range.startLineNumber, match.range.startColumn});
            const auto end = buffer_.GetOffsetAt(Position{match.range.endLineNumber, match.range.endColumn});
            edits.push_back(OffsetEdit{start, end, std::u16string(replacement)});
        }
        ApplyOffsetEdits(std::move(edits), true);
        return matches.size();
    }

    std::optional<BracketMatch> TextModel::FindMatchingBracket(Position position) const
    {
        const auto length = buffer_.Length();
        if (length == 0)
        {
            return std::nullopt;
        }

        const auto offset = buffer_.GetOffsetAt(position);
        std::size_t bracketOffset = std::numeric_limits<std::size_t>::max();
        char16_t bracket{};

        // The common render path asks for a bracket match even when the caret is next to
        // ordinary text. Probe only the one or two candidate UTF-16 code units first; do
        // not materialize the entire piece tree unless there is actually a bracket to scan.
        if (offset < length)
        {
            const auto probe = buffer_.GetText(offset, 1);
            if (!probe.empty() && (IsOpenBracket(probe[0]) || IsCloseBracket(probe[0])))
            {
                bracketOffset = offset;
                bracket = probe[0];
            }
        }
        if (bracketOffset == std::numeric_limits<std::size_t>::max() && offset > 0)
        {
            const auto probe = buffer_.GetText(offset - 1, 1);
            if (!probe.empty() && (IsOpenBracket(probe[0]) || IsCloseBracket(probe[0])))
            {
                bracketOffset = offset - 1;
                bracket = probe[0];
            }
        }
        if (bracketOffset == std::numeric_limits<std::size_t>::max())
        {
            return std::nullopt;
        }

        const auto target = MatchingBracket(bracket);
        std::size_t matchOffset = std::numeric_limits<std::size_t>::max();
        int depth = 0;

        if (IsOpenBracket(bracket))
        {
            const auto tail = buffer_.GetText(bracketOffset, length - bracketOffset);
            for (std::size_t i = 1; i < tail.size(); ++i)
            {
                if (tail[i] == bracket)
                {
                    ++depth;
                }
                else if (tail[i] == target)
                {
                    if (depth == 0)
                    {
                        matchOffset = bracketOffset + i;
                        break;
                    }
                    --depth;
                }
            }
        }
        else
        {
            const auto head = buffer_.GetText(0, bracketOffset + 1);
            for (std::size_t i = bracketOffset; i-- > 0; )
            {
                if (head[i] == bracket)
                {
                    ++depth;
                }
                else if (head[i] == target)
                {
                    if (depth == 0)
                    {
                        matchOffset = i;
                        break;
                    }
                    --depth;
                }
            }
        }

        if (matchOffset == std::numeric_limits<std::size_t>::max())
        {
            return std::nullopt;
        }
        return BracketMatch{buffer_.GetPositionAt(bracketOffset), buffer_.GetPositionAt(matchOffset)};
    }

    DecorationCollection& TextModel::Decorations() noexcept
    {
        return decorations_;
    }

    const DecorationCollection& TextModel::Decorations() const noexcept
    {
        return decorations_;
    }

    void TextModel::SetValue(std::u16string text)
    {
        const auto oldLength = buffer_.Length();
        const auto oldEnd = buffer_.GetPositionAt(oldLength);
        std::vector<std::pair<DecorationId, std::pair<std::size_t, std::size_t>>> oldDecorationOffsets;
        oldDecorationOffsets.reserve(decorations_.All().size());
        for (const auto& decoration : decorations_.All())
        {
            oldDecorationOffsets.push_back({decoration.id, {
                buffer_.GetOffsetAt(Position{decoration.range.startLineNumber, decoration.range.startColumn}),
                buffer_.GetOffsetAt(Position{decoration.range.endLineNumber, decoration.range.endColumn})
            }});
        }

        const auto replacement = text;
        buffer_.SetText(std::move(text));
        undoRedo_.Clear();
        ++versionId_;
        TransformDecorations({OffsetEdit{0, oldLength, replacement}}, oldDecorationOffsets);

        ModelContentChangedEvent event{};
        event.versionId = versionId_;
        event.changes.push_back(ModelContentChange{
            .range = Range{1, 1, oldEnd.lineNumber, oldEnd.column},
            .rangeOffset = 0,
            .rangeLength = oldLength,
            .text = buffer_.GetText(),
        });
        didChangeContent_.Emit(event);
    }

    void TextModel::ApplyEdits(std::vector<TextEdit> edits, bool recordUndo)
    {
        std::vector<OffsetEdit> offsetEdits;
        offsetEdits.reserve(edits.size());
        for (const auto& edit : edits)
        {
            const auto start = buffer_.GetOffsetAt(Position{edit.range.startLineNumber, edit.range.startColumn});
            const auto end = buffer_.GetOffsetAt(Position{edit.range.endLineNumber, edit.range.endColumn});
            offsetEdits.push_back(OffsetEdit{start, end, edit.text});
        }
        ApplyOffsetEdits(std::move(offsetEdits), recordUndo);
    }

    void TextModel::ApplyOffsetEdits(std::vector<OffsetEdit> edits, bool recordUndo)
    {
        ApplyOffsetEditsInternal(std::move(edits), recordUndo, false, false);
    }

    bool TextModel::CanUndo() const noexcept
    {
        return undoRedo_.CanUndo();
    }

    bool TextModel::CanRedo() const noexcept
    {
        return undoRedo_.CanRedo();
    }

    bool TextModel::Undo()
    {
        if (undoRedo_.InTransaction() || !undoRedo_.CanUndo())
        {
            return false;
        }
        auto element = undoRedo_.PopUndo();
        for (auto iterator = element.steps.rbegin(); iterator != element.steps.rend(); ++iterator)
        {
            ApplyOffsetEditsInternal(iterator->inverse, false, true, false);
        }
        undoRedo_.PushRedo(std::move(element));
        return true;
    }

    bool TextModel::Redo()
    {
        if (undoRedo_.InTransaction() || !undoRedo_.CanRedo())
        {
            return false;
        }
        auto element = undoRedo_.PopRedo();
        for (const auto& step : element.steps)
        {
            ApplyOffsetEditsInternal(step.forward, false, false, true);
        }
        undoRedo_.PushUndoFromRedo(std::move(element));
        return true;
    }

    void TextModel::BeginUndoTransaction()
    {
        undoRedo_.BeginTransaction();
    }

    void TextModel::EndUndoTransaction()
    {
        undoRedo_.EndTransaction();
    }

    bool TextModel::RollbackUndoTransaction()
    {
        if (!undoRedo_.InTransaction())
        {
            return false;
        }
        auto pending = undoRedo_.RollbackTransaction();
        for (auto iterator = pending.steps.rbegin(); iterator != pending.steps.rend(); ++iterator)
        {
            ApplyOffsetEditsInternal(iterator->inverse, false, true, false);
        }
        return true;
    }

    void TextModel::PushUndoStop()
    {
        undoRedo_.PushUndoStop();
    }

    bool TextModel::InUndoTransaction() const noexcept
    {
        return undoRedo_.InTransaction();
    }

    EventSource<ModelContentChangedEvent>& TextModel::OnDidChangeContent() noexcept
    {
        return didChangeContent_;
    }

    void TextModel::ApplyOffsetEditsInternal(std::vector<OffsetEdit> edits, bool recordUndo, bool isUndoing, bool isRedoing)
    {
        if (edits.empty())
        {
            return;
        }

        std::sort(edits.begin(), edits.end(), [](const OffsetEdit& a, const OffsetEdit& b)
        {
            if (a.start != b.start)
            {
                return a.start < b.start;
            }
            return a.end < b.end;
        });

        const auto oldLength = buffer_.Length();
        std::size_t previousEnd = 0;
        bool first = true;
        for (const auto& edit : edits)
        {
            if (edit.start > edit.end || edit.end > oldLength)
            {
                throw std::out_of_range("Edit range is invalid.");
            }
            if (!first && edit.start < previousEnd)
            {
                throw std::invalid_argument("Overlapping edits are not supported in one edit batch.");
            }
            previousEnd = edit.end;
            first = false;
        }

        std::vector<std::pair<DecorationId, std::pair<std::size_t, std::size_t>>> oldDecorationOffsets;
        oldDecorationOffsets.reserve(decorations_.All().size());
        for (const auto& decoration : decorations_.All())
        {
            oldDecorationOffsets.push_back({decoration.id, {
                buffer_.GetOffsetAt(Position{decoration.range.startLineNumber, decoration.range.startColumn}),
                buffer_.GetOffsetAt(Position{decoration.range.endLineNumber, decoration.range.endColumn})
            }});
        }

        EditBatch batch{};
        batch.forward = edits;
        batch.inverse.reserve(edits.size());

        ModelContentChangedEvent event{};
        event.versionId = versionId_ + 1;
        event.isUndoing = isUndoing;
        event.isRedoing = isRedoing;
        event.changes.reserve(edits.size());

        std::ptrdiff_t cumulativeDelta = 0;
        for (const auto& edit : edits)
        {
            const auto oldText = buffer_.GetText(edit.start, edit.end - edit.start);
            const auto startPosition = buffer_.GetPositionAt(edit.start);
            const auto endPosition = buffer_.GetPositionAt(edit.end);

            const auto postStartSigned = static_cast<std::ptrdiff_t>(edit.start) + cumulativeDelta;
            if (postStartSigned < 0)
            {
                throw std::logic_error("Internal edit transformation produced a negative offset.");
            }
            const auto postStart = static_cast<std::size_t>(postStartSigned);
            batch.inverse.push_back(OffsetEdit{postStart, postStart + edit.text.size(), oldText});

            event.changes.push_back(ModelContentChange{
                .range = Range{startPosition.lineNumber, startPosition.column, endPosition.lineNumber, endPosition.column},
                .rangeOffset = edit.start,
                .rangeLength = edit.end - edit.start,
                .text = edit.text,
            });

            cumulativeDelta += static_cast<std::ptrdiff_t>(edit.text.size()) - static_cast<std::ptrdiff_t>(edit.end - edit.start);
        }

        for (auto iterator = edits.rbegin(); iterator != edits.rend(); ++iterator)
        {
            buffer_.Replace(iterator->start, iterator->end, iterator->text);
        }

        TransformDecorations(edits, oldDecorationOffsets);
        ++versionId_;
        if (recordUndo)
        {
            undoRedo_.Push(std::move(batch));
        }
        didChangeContent_.Emit(event);
    }

    void TextModel::TransformDecorations(
        const std::vector<OffsetEdit>& edits,
        const std::vector<std::pair<DecorationId, std::pair<std::size_t, std::size_t>>>& oldDecorationOffsets)
    {
        for (const auto& item : oldDecorationOffsets)
        {
            const auto decoration = decorations_.Get(item.first);
            if (!decoration)
            {
                continue;
            }

            auto start = TransformBoundary(item.second.first, true, decoration->options.stickiness, edits);
            auto end = TransformBoundary(item.second.second, false, decoration->options.stickiness, edits);
            start = std::min(start, buffer_.Length());
            end = std::min(end, buffer_.Length());
            if (end < start)
            {
                end = start;
            }
            const auto startPosition = buffer_.GetPositionAt(start);
            const auto endPosition = buffer_.GetPositionAt(end);
            decorations_.SetRange(item.first, Range{
                startPosition.lineNumber,
                startPosition.column,
                endPosition.lineNumber,
                endPosition.column,
            });
        }
    }
}
