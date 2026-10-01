#include "MonacoNative/Core/DiffEngine.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <limits>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace MonacoNative::Core
{
    namespace
    {
        struct SequenceOp final
        {
            DiffKind kind{DiffKind::Equal};
            std::size_t originalIndex{};
            std::size_t modifiedIndex{};
        };

        std::vector<std::u16string> SplitLines(std::u16string_view text)
        {
            std::vector<std::u16string> lines;
            std::size_t start = 0;
            for (std::size_t i = 0; i < text.size(); ++i)
            {
                if (text[i] != u'\n')
                {
                    continue;
                }
                auto line = std::u16string(text.substr(start, i - start));
                if (!line.empty() && line.back() == u'\r')
                {
                    line.pop_back();
                }
                lines.push_back(std::move(line));
                start = i + 1;
            }
            lines.emplace_back(text.substr(start));
            if (!lines.back().empty() && lines.back().back() == u'\r')
            {
                lines.back().pop_back();
            }
            return lines;
        }

        std::u16string_view Trim(std::u16string_view value) noexcept
        {
            auto isWhitespace = [](char16_t ch) noexcept
            {
                return ch == u' ' || ch == u'\t' || ch == u'\r' || ch == u'\n';
            };
            std::size_t first = 0;
            while (first < value.size() && isWhitespace(value[first]))
            {
                ++first;
            }
            std::size_t last = value.size();
            while (last > first && isWhitespace(value[last - 1]))
            {
                --last;
            }
            return value.substr(first, last - first);
        }

        template<typename Equals>
        bool HasAnyCommonItem(
            std::size_t originalCount,
            std::size_t modifiedCount,
            Equals&& equals)
        {
            // Fast quadratic probe only for small sides; larger inputs are handled by
            // the hashed line-specific probe before Myers is invoked.
            if (originalCount > 256 || modifiedCount > 256)
            {
                return true;
            }
            for (std::size_t i = 0; i < originalCount; ++i)
            {
                for (std::size_t j = 0; j < modifiedCount; ++j)
                {
                    if (equals(i, j))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        template<typename Equals>
        std::vector<SequenceOp> ComputeMyers(
            std::size_t originalCount,
            std::size_t modifiedCount,
            Equals&& equals,
            std::size_t traceCellBudget = 16'000'000)
        {
            std::vector<SequenceOp> result;
            if (originalCount == 0)
            {
                result.reserve(modifiedCount);
                for (std::size_t j = 0; j < modifiedCount; ++j)
                {
                    result.push_back({DiffKind::Insert, 0, j});
                }
                return result;
            }
            if (modifiedCount == 0)
            {
                result.reserve(originalCount);
                for (std::size_t i = 0; i < originalCount; ++i)
                {
                    result.push_back({DiffKind::Delete, i, 0});
                }
                return result;
            }

            // Avoid allocating the full Myers trace when the two short sequences have
            // no overlap at all. This is a common generated-file/change-set case.
            if (!HasAnyCommonItem(originalCount, modifiedCount, equals))
            {
                result.reserve(originalCount + modifiedCount);
                for (std::size_t i = 0; i < originalCount; ++i)
                {
                    result.push_back({DiffKind::Delete, i, 0});
                }
                for (std::size_t j = 0; j < modifiedCount; ++j)
                {
                    result.push_back({DiffKind::Insert, originalCount, j});
                }
                return result;
            }

            const auto maxDistance = originalCount + modifiedCount;
            const auto width = (maxDistance * 2) + 3;
            const auto offset = static_cast<std::ptrdiff_t>(maxDistance + 1);
            std::vector<std::ptrdiff_t> v(width, -1);
            v[static_cast<std::size_t>(offset + 1)] = 0;
            std::vector<std::vector<std::ptrdiff_t>> trace;
            trace.reserve(std::min<std::size_t>(maxDistance + 1, 4096));

            std::size_t finalDistance = 0;
            bool found = false;
            for (std::size_t d = 0; d <= maxDistance; ++d)
            {
                if ((trace.size() + 1) * width > traceCellBudget)
                {
                    // Deterministic bounded fallback. It intentionally prefers a coarse
                    // delete/insert hunk over exhausting memory on pathological inputs.
                    result.reserve(originalCount + modifiedCount);
                    for (std::size_t i = 0; i < originalCount; ++i)
                    {
                        result.push_back({DiffKind::Delete, i, 0});
                    }
                    for (std::size_t j = 0; j < modifiedCount; ++j)
                    {
                        result.push_back({DiffKind::Insert, originalCount, j});
                    }
                    return result;
                }

                const auto distance = static_cast<std::ptrdiff_t>(d);
                for (std::ptrdiff_t k = -distance; k <= distance; k += 2)
                {
                    const auto index = static_cast<std::size_t>(offset + k);
                    std::ptrdiff_t x = 0;
                    if (k == -distance || (k != distance && v[index - 1] < v[index + 1]))
                    {
                        x = v[index + 1];
                    }
                    else
                    {
                        x = v[index - 1] + 1;
                    }
                    auto y = x - k;
                    while (x >= 0 && y >= 0 &&
                           static_cast<std::size_t>(x) < originalCount &&
                           static_cast<std::size_t>(y) < modifiedCount &&
                           equals(static_cast<std::size_t>(x), static_cast<std::size_t>(y)))
                    {
                        ++x;
                        ++y;
                    }
                    v[index] = x;
                    if (static_cast<std::size_t>(x) >= originalCount &&
                        static_cast<std::size_t>(y) >= modifiedCount)
                    {
                        found = true;
                        finalDistance = d;
                        break;
                    }
                }
                trace.push_back(v);
                if (found)
                {
                    break;
                }
            }

            if (!found)
            {
                return result;
            }

            std::size_t x = originalCount;
            std::size_t y = modifiedCount;
            std::vector<SequenceOp> reversed;
            reversed.reserve(originalCount + modifiedCount);

            for (std::size_t d = finalDistance; d > 0; --d)
            {
                const auto& previous = trace[d - 1];
                const auto distance = static_cast<std::ptrdiff_t>(d);
                const auto k = static_cast<std::ptrdiff_t>(x) - static_cast<std::ptrdiff_t>(y);
                const auto index = static_cast<std::size_t>(offset + k);
                std::ptrdiff_t previousK = 0;
                if (k == -distance || (k != distance && previous[index - 1] < previous[index + 1]))
                {
                    previousK = k + 1;
                }
                else
                {
                    previousK = k - 1;
                }
                const auto previousXValue = previous[static_cast<std::size_t>(offset + previousK)];
                const auto previousX = static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, previousXValue));
                const auto previousYValue = previousXValue - previousK;
                const auto previousY = static_cast<std::size_t>(std::max<std::ptrdiff_t>(0, previousYValue));

                while (x > previousX && y > previousY)
                {
                    --x;
                    --y;
                    reversed.push_back({DiffKind::Equal, x, y});
                }

                if (x == previousX)
                {
                    if (y > 0)
                    {
                        --y;
                        reversed.push_back({DiffKind::Insert, x, y});
                    }
                }
                else if (x > 0)
                {
                    --x;
                    reversed.push_back({DiffKind::Delete, x, y});
                }
            }

            while (x > 0 && y > 0)
            {
                --x;
                --y;
                reversed.push_back({DiffKind::Equal, x, y});
            }
            while (x > 0)
            {
                --x;
                reversed.push_back({DiffKind::Delete, x, 0});
            }
            while (y > 0)
            {
                --y;
                reversed.push_back({DiffKind::Insert, 0, y});
            }

            std::reverse(reversed.begin(), reversed.end());
            return reversed;
        }

        std::vector<SequenceOp> ComputeLineOps(
            const std::vector<std::u16string>& original,
            const std::vector<std::u16string>& modified,
            bool ignoreTrimWhitespace)
        {
            // Large completely unrelated files are best handled without a full Myers trace.
            if (original.size() > 256 || modified.size() > 256)
            {
                std::unordered_set<std::u16string> originalSet;
                originalSet.reserve(original.size());
                for (const auto& line : original)
                {
                    const auto view = ignoreTrimWhitespace ? Trim(line) : std::u16string_view(line);
                    originalSet.emplace(view);
                }
                bool anyCommon = false;
                for (const auto& line : modified)
                {
                    const auto view = ignoreTrimWhitespace ? Trim(line) : std::u16string_view(line);
                    if (originalSet.find(std::u16string(view)) != originalSet.end())
                    {
                        anyCommon = true;
                        break;
                    }
                }
                if (!anyCommon)
                {
                    std::vector<SequenceOp> result;
                    result.reserve(original.size() + modified.size());
                    for (std::size_t i = 0; i < original.size(); ++i)
                    {
                        result.push_back({DiffKind::Delete, i, 0});
                    }
                    for (std::size_t j = 0; j < modified.size(); ++j)
                    {
                        result.push_back({DiffKind::Insert, original.size(), j});
                    }
                    return result;
                }
            }

            return ComputeMyers(original.size(), modified.size(), [&](std::size_t i, std::size_t j)
            {
                if (ignoreTrimWhitespace)
                {
                    return Trim(original[i]) == Trim(modified[j]);
                }
                return original[i] == modified[j];
            });
        }

        std::vector<DiffInnerChange> ComputeInnerChanges(
            std::u16string_view original,
            std::u16string_view modified,
            std::int32_t originalLine,
            std::int32_t modifiedLine)
        {
            if (original == modified)
            {
                return {};
            }

            const auto ops = ComputeMyers(original.size(), modified.size(), [&](std::size_t i, std::size_t j)
            {
                return original[i] == modified[j];
            }, 2'000'000);

            std::vector<DiffInnerChange> changes;
            std::size_t originalColumn = 1;
            std::size_t modifiedColumn = 1;
            std::size_t index = 0;
            while (index < ops.size())
            {
                if (ops[index].kind == DiffKind::Equal)
                {
                    ++originalColumn;
                    ++modifiedColumn;
                    ++index;
                    continue;
                }

                const auto originalStart = originalColumn;
                const auto modifiedStart = modifiedColumn;
                while (index < ops.size() && ops[index].kind != DiffKind::Equal)
                {
                    if (ops[index].kind == DiffKind::Delete)
                    {
                        ++originalColumn;
                    }
                    else if (ops[index].kind == DiffKind::Insert)
                    {
                        ++modifiedColumn;
                    }
                    ++index;
                }
                changes.push_back(DiffInnerChange{
                    originalLine,
                    static_cast<std::int32_t>(originalStart),
                    static_cast<std::int32_t>(originalColumn),
                    modifiedLine,
                    static_cast<std::int32_t>(modifiedStart),
                    static_cast<std::int32_t>(modifiedColumn),
                });
            }
            return changes;
        }

        bool EqualLineBlock(
            const std::vector<std::u16string>& original,
            std::int32_t originalStart,
            std::int32_t originalEndExclusive,
            const std::vector<std::u16string>& modified,
            std::int32_t modifiedStart,
            std::int32_t modifiedEndExclusive,
            bool ignoreTrimWhitespace)
        {
            const auto originalCount = originalEndExclusive - originalStart;
            const auto modifiedCount = modifiedEndExclusive - modifiedStart;
            if (originalCount != modifiedCount || originalCount <= 0)
            {
                return false;
            }
            for (std::int32_t offset = 0; offset < originalCount; ++offset)
            {
                const auto& left = original[static_cast<std::size_t>(originalStart - 1 + offset)];
                const auto& right = modified[static_cast<std::size_t>(modifiedStart - 1 + offset)];
                if (ignoreTrimWhitespace ? Trim(left) != Trim(right) : left != right)
                {
                    return false;
                }
            }
            return true;
        }

        std::vector<DiffMove> DetectMoves(
            const std::vector<DiffChange>& changes,
            const std::vector<std::u16string>& original,
            const std::vector<std::u16string>& modified,
            bool ignoreTrimWhitespace)
        {
            struct Candidate final
            {
                std::int32_t start{};
                std::int32_t endExclusive{};
                std::size_t changeIndex{};
            };

            std::vector<Candidate> deletions;
            std::vector<Candidate> insertions;
            for (std::size_t i = 0; i < changes.size(); ++i)
            {
                const auto& change = changes[i];
                if (change.originalEndLineExclusive > change.originalStartLine)
                {
                    deletions.push_back({change.originalStartLine, change.originalEndLineExclusive, i});
                }
                if (change.modifiedEndLineExclusive > change.modifiedStartLine)
                {
                    insertions.push_back({change.modifiedStartLine, change.modifiedEndLineExclusive, i});
                }
            }

            std::vector<DiffMove> moves;
            std::vector<bool> usedInsertions(insertions.size(), false);
            for (const auto& deletion : deletions)
            {
                const auto length = deletion.endExclusive - deletion.start;
                // A single short line is too noisy to classify as a move. A long single
                // line is still useful for function declarations or generated records.
                const auto singleLongLine = length == 1 &&
                    original[static_cast<std::size_t>(deletion.start - 1)].size() >= 32;
                if (length < 2 && !singleLongLine)
                {
                    continue;
                }

                std::size_t best = std::numeric_limits<std::size_t>::max();
                for (std::size_t j = 0; j < insertions.size(); ++j)
                {
                    if (usedInsertions[j] || insertions[j].changeIndex == deletion.changeIndex)
                    {
                        continue;
                    }
                    const auto& insertion = insertions[j];
                    if (!EqualLineBlock(
                            original, deletion.start, deletion.endExclusive,
                            modified, insertion.start, insertion.endExclusive,
                            ignoreTrimWhitespace))
                    {
                        continue;
                    }
                    best = j;
                    break;
                }
                if (best == std::numeric_limits<std::size_t>::max())
                {
                    continue;
                }
                usedInsertions[best] = true;
                const auto& insertion = insertions[best];
                moves.push_back(DiffMove{
                    deletion.start,
                    deletion.endExclusive,
                    insertion.start,
                    insertion.endExclusive,
                });
            }
            return moves;
        }
    }

    DiffResult DiffEngine::ComputeDiff(
        std::u16string_view original,
        std::u16string_view modified,
        DiffOptions options)
    {
        const auto originalLines = SplitLines(original);
        const auto modifiedLines = SplitLines(modified);
        const auto ops = ComputeLineOps(originalLines, modifiedLines, options.ignoreTrimWhitespace);

        DiffResult result;
        result.lines.reserve(ops.size());
        for (const auto& op : ops)
        {
            switch (op.kind)
            {
            case DiffKind::Equal:
                result.lines.push_back(DiffLine{
                    DiffKind::Equal,
                    static_cast<std::int32_t>(op.originalIndex + 1),
                    static_cast<std::int32_t>(op.modifiedIndex + 1),
                    originalLines[op.originalIndex],
                });
                break;
            case DiffKind::Delete:
                result.lines.push_back(DiffLine{
                    DiffKind::Delete,
                    static_cast<std::int32_t>(op.originalIndex + 1),
                    0,
                    originalLines[op.originalIndex],
                });
                break;
            case DiffKind::Insert:
                result.lines.push_back(DiffLine{
                    DiffKind::Insert,
                    0,
                    static_cast<std::int32_t>(op.modifiedIndex + 1),
                    modifiedLines[op.modifiedIndex],
                });
                break;
            }
        }

        std::int32_t originalLine = 1;
        std::int32_t modifiedLine = 1;
        std::size_t index = 0;
        while (index < ops.size())
        {
            if (ops[index].kind == DiffKind::Equal)
            {
                ++originalLine;
                ++modifiedLine;
                ++index;
                continue;
            }

            DiffChange change{};
            change.originalStartLine = originalLine;
            change.modifiedStartLine = modifiedLine;
            while (index < ops.size() && ops[index].kind != DiffKind::Equal)
            {
                if (ops[index].kind == DiffKind::Delete)
                {
                    ++originalLine;
                }
                else if (ops[index].kind == DiffKind::Insert)
                {
                    ++modifiedLine;
                }
                ++index;
            }
            change.originalEndLineExclusive = originalLine;
            change.modifiedEndLineExclusive = modifiedLine;

            const auto originalCount = change.originalEndLineExclusive - change.originalStartLine;
            const auto modifiedCount = change.modifiedEndLineExclusive - change.modifiedStartLine;
            const auto paired = std::min(originalCount, modifiedCount);
            for (std::int32_t lineOffset = 0; lineOffset < paired; ++lineOffset)
            {
                const auto originalLineNumber = change.originalStartLine + lineOffset;
                const auto modifiedLineNumber = change.modifiedStartLine + lineOffset;
                auto inner = ComputeInnerChanges(
                    originalLines[static_cast<std::size_t>(originalLineNumber - 1)],
                    modifiedLines[static_cast<std::size_t>(modifiedLineNumber - 1)],
                    originalLineNumber,
                    modifiedLineNumber);
                change.innerChanges.insert(change.innerChanges.end(), inner.begin(), inner.end());
            }
            result.changes.push_back(std::move(change));
        }

        if (options.computeMoves)
        {
            result.moves = DetectMoves(result.changes, originalLines, modifiedLines, options.ignoreTrimWhitespace);
        }
        return result;
    }

    std::vector<DiffLine> DiffEngine::ComputeLineDiff(
        std::u16string_view original,
        std::u16string_view modified)
    {
        return ComputeDiff(original, modified, DiffOptions{false, false}).lines;
    }
}
