#include "MonacoNative/Core/PieceTreeTextBuffer.h"

#include <algorithm>
#include <limits>
#include <stdexcept>

namespace MonacoNative::Core
{
    namespace
    {
        std::uint64_t SplitMix64(std::uint64_t value) noexcept
        {
            value += 0x9e3779b97f4a7c15ULL;
            value = (value ^ (value >> 30U)) * 0xbf58476d1ce4e5b9ULL;
            value = (value ^ (value >> 27U)) * 0x94d049bb133111ebULL;
            return value ^ (value >> 31U);
        }
    }

    PieceTreeTextBuffer::PieceTreeTextBuffer() = default;

    PieceTreeTextBuffer::PieceTreeTextBuffer(std::u16string text)
    {
        SetText(std::move(text));
    }

    PieceTreeTextBuffer::PieceTreeTextBuffer(PieceTreeTextBuffer&&) noexcept = default;
    PieceTreeTextBuffer& PieceTreeTextBuffer::operator=(PieceTreeTextBuffer&&) noexcept = default;
    PieceTreeTextBuffer::~PieceTreeTextBuffer() = default;

    void PieceTreeTextBuffer::SetText(std::u16string text)
    {
        originalBuffer_ = std::move(text);
        addBuffer_.clear();
        root_.reset();
        nodeSequence_ = 1;
        RebuildBufferLineFeeds(BufferKind::Original);
        addLineFeeds_.clear();

        if (!originalBuffer_.empty())
        {
            Piece piece{
                .buffer = BufferKind::Original,
                .start = 0,
                .length = originalBuffer_.size(),
                .lineFeeds = CountLineFeeds(BufferKind::Original, 0, originalBuffer_.size()),
            };
            root_ = MakeNode(piece);
        }

    }

    void PieceTreeTextBuffer::Insert(std::size_t offset, std::u16string_view text)
    {
        if (offset > Length())
        {
            throw std::out_of_range("Insert offset exceeds text length.");
        }
        if (text.empty())
        {
            return;
        }

        const auto addStart = addBuffer_.size();
        addBuffer_.append(text);
        AppendBufferLineFeeds(BufferKind::Add, addStart, text);

        Piece piece{
            .buffer = BufferKind::Add,
            .start = addStart,
            .length = text.size(),
            .lineFeeds = CountLineFeeds(BufferKind::Add, addStart, text.size()),
        };

        auto [left, right] = Split(std::move(root_), offset);
        root_ = Merge(Merge(std::move(left), MakeNode(piece)), std::move(right));
    }

    void PieceTreeTextBuffer::Erase(std::size_t start, std::size_t end)
    {
        if (start > end || end > Length())
        {
            throw std::out_of_range("Erase range is invalid.");
        }
        if (start == end)
        {
            return;
        }

        auto [beforeAndRemoved, after] = Split(std::move(root_), end);
        auto [before, removed] = Split(std::move(beforeAndRemoved), start);
        removed.reset();
        root_ = Merge(std::move(before), std::move(after));
    }

    void PieceTreeTextBuffer::Replace(std::size_t start, std::size_t end, std::u16string_view text)
    {
        Erase(start, end);
        Insert(start, text);
    }

    std::size_t PieceTreeTextBuffer::Length() const noexcept
    {
        return SubtreeLength(root_);
    }

    std::size_t PieceTreeTextBuffer::LineCount() const
    {
        return SubtreeLineFeeds(root_) + 1;
    }

    std::u16string PieceTreeTextBuffer::GetText() const
    {
        std::u16string output;
        output.reserve(Length());
        AppendText(root_, output);
        return output;
    }

    std::u16string PieceTreeTextBuffer::GetText(std::size_t start, std::size_t length) const
    {
        if (start > Length())
        {
            throw std::out_of_range("Text range start exceeds text length.");
        }
        const auto safeLength = std::min(length, Length() - start);
        std::u16string output;
        output.reserve(safeLength);
        AppendTextRange(root_, 0, start, start + safeLength, output);
        return output;
    }

    std::u16string PieceTreeTextBuffer::GetLineContent(std::int32_t lineNumber) const
    {
        if (lineNumber < 1 || static_cast<std::size_t>(lineNumber) > LineCount())
        {
            throw std::out_of_range("Line number is invalid.");
        }

        const auto start = LineStartOffset(lineNumber);
        const auto end = ContentLineEndOffset(lineNumber);
        return GetText(start, end - start);
    }

    std::size_t PieceTreeTextBuffer::GetLineLength(std::int32_t lineNumber) const
    {
        if (lineNumber < 1 || static_cast<std::size_t>(lineNumber) > LineCount())
        {
            throw std::out_of_range("Line number is invalid.");
        }
        return ContentLineEndOffset(lineNumber) - LineStartOffset(lineNumber);
    }

    Position PieceTreeTextBuffer::GetPositionAt(std::size_t offset) const
    {
        offset = std::min(offset, Length());
        const auto precedingLineFeeds = CountLineFeedsBeforeOffset(offset);
        const auto lineStart = precedingLineFeeds == 0 ? 0 : FindLineFeedOffset(precedingLineFeeds - 1) + 1;
        return Position{
            .lineNumber = static_cast<std::int32_t>(precedingLineFeeds + 1),
            .column = static_cast<std::int32_t>(offset - lineStart + 1),
        };
    }

    std::size_t PieceTreeTextBuffer::GetOffsetAt(Position position) const
    {
        const auto lineCount = static_cast<std::int32_t>(LineCount());
        const auto clampedLine = std::clamp<std::int32_t>(position.lineNumber, 1, lineCount);
        const auto lineStart = LineStartOffset(clampedLine);
        const auto contentEnd = ContentLineEndOffset(clampedLine);
        const auto maxColumn = static_cast<std::int32_t>(contentEnd - lineStart + 1);
        const auto clampedColumn = std::clamp<std::int32_t>(position.column, 1, std::max(1, maxColumn));
        return lineStart + static_cast<std::size_t>(clampedColumn - 1);
    }

    const std::u16string& PieceTreeTextBuffer::Buffer(BufferKind kind) const noexcept
    {
        return kind == BufferKind::Original ? originalBuffer_ : addBuffer_;
    }

    const std::vector<std::size_t>& PieceTreeTextBuffer::BufferLineFeeds(BufferKind kind) const noexcept
    {
        return kind == BufferKind::Original ? originalLineFeeds_ : addLineFeeds_;
    }

    void PieceTreeTextBuffer::RebuildBufferLineFeeds(BufferKind kind)
    {
        auto& target = kind == BufferKind::Original ? originalLineFeeds_ : addLineFeeds_;
        target.clear();
        const auto& source = Buffer(kind);
        for (std::size_t i = 0; i < source.size(); ++i)
        {
            if (source[i] == u'\n')
            {
                target.push_back(i);
            }
        }
    }

    void PieceTreeTextBuffer::AppendBufferLineFeeds(BufferKind kind, std::size_t start, std::u16string_view text)
    {
        auto& target = kind == BufferKind::Original ? originalLineFeeds_ : addLineFeeds_;
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            if (text[i] == u'\n')
            {
                target.push_back(start + i);
            }
        }
    }

    std::size_t PieceTreeTextBuffer::CountLineFeeds(BufferKind kind, std::size_t start, std::size_t length) const
    {
        if (length == 0)
        {
            return 0;
        }
        const auto& feeds = BufferLineFeeds(kind);
        const auto first = std::lower_bound(feeds.begin(), feeds.end(), start);
        const auto last = std::lower_bound(first, feeds.end(), start + length);
        return static_cast<std::size_t>(std::distance(first, last));
    }

    std::size_t PieceTreeTextBuffer::CountLineFeedsInPiecePrefix(const Piece& piece, std::size_t prefixLength) const
    {
        return CountLineFeeds(piece.buffer, piece.start, std::min(prefixLength, piece.length));
    }

    std::size_t PieceTreeTextBuffer::FindLineFeedInPiece(const Piece& piece, std::size_t ordinal) const
    {
        const auto& feeds = BufferLineFeeds(piece.buffer);
        const auto first = std::lower_bound(feeds.begin(), feeds.end(), piece.start);
        const auto available = static_cast<std::size_t>(std::distance(first, std::lower_bound(first, feeds.end(), piece.start + piece.length)));
        if (ordinal >= available)
        {
            throw std::out_of_range("Line feed ordinal exceeds piece line feeds.");
        }
        return *(first + static_cast<std::ptrdiff_t>(ordinal));
    }

    std::size_t PieceTreeTextBuffer::FindLineFeedOffset(std::size_t ordinal) const
    {
        if (ordinal >= SubtreeLineFeeds(root_))
        {
            throw std::out_of_range("Line feed ordinal exceeds buffer line feeds.");
        }

        const Node* node = root_.get();
        std::size_t baseOffset = 0;
        auto target = ordinal;
        while (node)
        {
            const auto leftFeeds = SubtreeLineFeeds(node->left);
            const auto leftLength = SubtreeLength(node->left);
            if (target < leftFeeds)
            {
                node = node->left.get();
                continue;
            }

            target -= leftFeeds;
            baseOffset += leftLength;
            if (target < node->piece.lineFeeds)
            {
                const auto bufferOffset = FindLineFeedInPiece(node->piece, target);
                return baseOffset + (bufferOffset - node->piece.start);
            }

            target -= node->piece.lineFeeds;
            baseOffset += node->piece.length;
            node = node->right.get();
        }
        throw std::out_of_range("Line feed ordinal could not be resolved.");
    }

    std::size_t PieceTreeTextBuffer::CountLineFeedsBeforeOffset(std::size_t offset) const
    {
        auto remaining = std::min(offset, Length());
        const Node* node = root_.get();
        std::size_t count = 0;
        while (node)
        {
            const auto leftLength = SubtreeLength(node->left);
            if (remaining <= leftLength)
            {
                node = node->left.get();
                continue;
            }

            count += SubtreeLineFeeds(node->left);
            remaining -= leftLength;
            if (remaining <= node->piece.length)
            {
                count += CountLineFeedsInPiecePrefix(node->piece, remaining);
                return count;
            }

            count += node->piece.lineFeeds;
            remaining -= node->piece.length;
            node = node->right.get();
        }
        return count;
    }

    std::size_t PieceTreeTextBuffer::LineStartOffset(std::int32_t lineNumber) const
    {
        return lineNumber <= 1 ? 0 : FindLineFeedOffset(static_cast<std::size_t>(lineNumber - 2)) + 1;
    }

    std::size_t PieceTreeTextBuffer::RawLineEndOffset(std::int32_t lineNumber) const
    {
        const auto lineCount = static_cast<std::int32_t>(LineCount());
        return lineNumber < lineCount ? FindLineFeedOffset(static_cast<std::size_t>(lineNumber - 1)) : Length();
    }

    std::size_t PieceTreeTextBuffer::ContentLineEndOffset(std::int32_t lineNumber) const
    {
        const auto start = LineStartOffset(lineNumber);
        auto end = RawLineEndOffset(lineNumber);
        if (end > start)
        {
            const auto tail = GetText(end - 1, 1);
            if (!tail.empty() && tail.front() == u'\r')
            {
                --end;
            }
        }
        return end;
    }

    PieceTreeTextBuffer::NodePtr PieceTreeTextBuffer::MakeNode(Piece piece)
    {
        auto node = std::make_unique<Node>();
        node->piece = piece;
        node->priority = NextPriority();
        Update(*node);
        return node;
    }

    std::size_t PieceTreeTextBuffer::SubtreeLength(const NodePtr& node) noexcept
    {
        return node ? node->subtreeLength : 0;
    }

    std::size_t PieceTreeTextBuffer::SubtreeLineFeeds(const NodePtr& node) noexcept
    {
        return node ? node->subtreeLineFeeds : 0;
    }

    void PieceTreeTextBuffer::Update(Node& node) noexcept
    {
        node.subtreeLength = SubtreeLength(node.left) + node.piece.length + SubtreeLength(node.right);
        node.subtreeLineFeeds = SubtreeLineFeeds(node.left) + node.piece.lineFeeds + SubtreeLineFeeds(node.right);
    }

    PieceTreeTextBuffer::NodePtr PieceTreeTextBuffer::Merge(NodePtr left, NodePtr right)
    {
        if (!left)
        {
            return right;
        }
        if (!right)
        {
            return left;
        }

        if (left->priority > right->priority)
        {
            left->right = Merge(std::move(left->right), std::move(right));
            Update(*left);
            return left;
        }

        right->left = Merge(std::move(left), std::move(right->left));
        Update(*right);
        return right;
    }

    std::pair<PieceTreeTextBuffer::NodePtr, PieceTreeTextBuffer::NodePtr>
    PieceTreeTextBuffer::Split(NodePtr root, std::size_t offset)
    {
        if (!root)
        {
            return {nullptr, nullptr};
        }

        const auto leftLength = SubtreeLength(root->left);
        const auto pieceStart = leftLength;
        const auto pieceEnd = leftLength + root->piece.length;

        if (offset < pieceStart)
        {
            auto [left, middle] = Split(std::move(root->left), offset);
            root->left = std::move(middle);
            Update(*root);
            return {std::move(left), std::move(root)};
        }

        if (offset > pieceEnd)
        {
            auto [middle, right] = Split(std::move(root->right), offset - pieceEnd);
            root->right = std::move(middle);
            Update(*root);
            return {std::move(root), std::move(right)};
        }

        if (offset == pieceStart)
        {
            auto left = std::move(root->left);
            root->left.reset();
            Update(*root);
            return {std::move(left), std::move(root)};
        }

        if (offset == pieceEnd)
        {
            auto right = std::move(root->right);
            root->right.reset();
            Update(*root);
            return {std::move(root), std::move(right)};
        }

        const auto splitInsidePiece = offset - pieceStart;
        Piece leftPiece = root->piece;
        leftPiece.length = splitInsidePiece;
        leftPiece.lineFeeds = CountLineFeeds(leftPiece.buffer, leftPiece.start, leftPiece.length);

        Piece rightPiece = root->piece;
        rightPiece.start += splitInsidePiece;
        rightPiece.length -= splitInsidePiece;
        rightPiece.lineFeeds = CountLineFeeds(rightPiece.buffer, rightPiece.start, rightPiece.length);

        auto leftTree = Merge(std::move(root->left), MakeNode(leftPiece));
        auto rightTree = Merge(MakeNode(rightPiece), std::move(root->right));
        return {std::move(leftTree), std::move(rightTree)};
    }

    void PieceTreeTextBuffer::AppendText(const NodePtr& node, std::u16string& output) const
    {
        if (!node)
        {
            return;
        }
        AppendText(node->left, output);
        const auto& source = Buffer(node->piece.buffer);
        output.append(source, node->piece.start, node->piece.length);
        AppendText(node->right, output);
    }

    void PieceTreeTextBuffer::AppendTextRange(
        const NodePtr& node,
        std::size_t baseOffset,
        std::size_t start,
        std::size_t end,
        std::u16string& output) const
    {
        if (!node || start >= end)
        {
            return;
        }

        const auto leftLength = SubtreeLength(node->left);
        const auto pieceStart = baseOffset + leftLength;
        const auto pieceEnd = pieceStart + node->piece.length;

        if (start < pieceStart)
        {
            AppendTextRange(node->left, baseOffset, start, std::min(end, pieceStart), output);
        }

        const auto overlapStart = std::max(start, pieceStart);
        const auto overlapEnd = std::min(end, pieceEnd);
        if (overlapStart < overlapEnd)
        {
            const auto& source = Buffer(node->piece.buffer);
            const auto localStart = overlapStart - pieceStart;
            output.append(source, node->piece.start + localStart, overlapEnd - overlapStart);
        }

        if (end > pieceEnd)
        {
            AppendTextRange(node->right, pieceEnd, std::max(start, pieceEnd), end, output);
        }
    }

    std::uint64_t PieceTreeTextBuffer::NextPriority() noexcept
    {
        return SplitMix64(nodeSequence_++);
    }
}
