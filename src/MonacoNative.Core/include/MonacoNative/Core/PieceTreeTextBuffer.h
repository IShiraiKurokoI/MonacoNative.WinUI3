#pragma once

#include "TextTypes.h"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace MonacoNative::Core
{
    // A native UTF-16 piece tree implemented as an implicit treap.
    // Offsets and columns intentionally use UTF-16 code units to preserve Monaco semantics.
    class PieceTreeTextBuffer final
    {
    public:
        PieceTreeTextBuffer();
        explicit PieceTreeTextBuffer(std::u16string text);
        PieceTreeTextBuffer(PieceTreeTextBuffer&&) noexcept;
        PieceTreeTextBuffer& operator=(PieceTreeTextBuffer&&) noexcept;
        ~PieceTreeTextBuffer();

        PieceTreeTextBuffer(const PieceTreeTextBuffer&) = delete;
        PieceTreeTextBuffer& operator=(const PieceTreeTextBuffer&) = delete;

        void SetText(std::u16string text);
        void Insert(std::size_t offset, std::u16string_view text);
        void Erase(std::size_t start, std::size_t end);
        void Replace(std::size_t start, std::size_t end, std::u16string_view text);

        [[nodiscard]] std::size_t Length() const noexcept;
        [[nodiscard]] std::size_t LineCount() const;
        [[nodiscard]] std::u16string GetText() const;
        [[nodiscard]] std::u16string GetText(std::size_t start, std::size_t length) const;
        [[nodiscard]] std::u16string GetLineContent(std::int32_t lineNumber) const;
        [[nodiscard]] std::size_t GetLineLength(std::int32_t lineNumber) const;
        [[nodiscard]] Position GetPositionAt(std::size_t offset) const;
        [[nodiscard]] std::size_t GetOffsetAt(Position position) const;

    private:
        enum class BufferKind : std::uint8_t
        {
            Original,
            Add,
        };

        struct Piece final
        {
            BufferKind buffer{BufferKind::Original};
            std::size_t start{};
            std::size_t length{};
            std::size_t lineFeeds{};
        };

        struct Node final
        {
            Piece piece{};
            std::uint64_t priority{};
            std::unique_ptr<Node> left{};
            std::unique_ptr<Node> right{};
            std::size_t subtreeLength{};
            std::size_t subtreeLineFeeds{};
        };

        using NodePtr = std::unique_ptr<Node>;

        [[nodiscard]] const std::u16string& Buffer(BufferKind kind) const noexcept;
        [[nodiscard]] const std::vector<std::size_t>& BufferLineFeeds(BufferKind kind) const noexcept;
        void RebuildBufferLineFeeds(BufferKind kind);
        void AppendBufferLineFeeds(BufferKind kind, std::size_t start, std::u16string_view text);
        [[nodiscard]] std::size_t CountLineFeeds(BufferKind kind, std::size_t start, std::size_t length) const;
        [[nodiscard]] std::size_t CountLineFeedsInPiecePrefix(const Piece& piece, std::size_t prefixLength) const;
        [[nodiscard]] std::size_t FindLineFeedInPiece(const Piece& piece, std::size_t ordinal) const;
        [[nodiscard]] std::size_t FindLineFeedOffset(std::size_t ordinal) const;
        [[nodiscard]] std::size_t CountLineFeedsBeforeOffset(std::size_t offset) const;
        [[nodiscard]] std::size_t LineStartOffset(std::int32_t lineNumber) const;
        [[nodiscard]] std::size_t RawLineEndOffset(std::int32_t lineNumber) const;
        [[nodiscard]] std::size_t ContentLineEndOffset(std::int32_t lineNumber) const;
        [[nodiscard]] NodePtr MakeNode(Piece piece);
        [[nodiscard]] static std::size_t SubtreeLength(const NodePtr& node) noexcept;
        [[nodiscard]] static std::size_t SubtreeLineFeeds(const NodePtr& node) noexcept;
        static void Update(Node& node) noexcept;
        [[nodiscard]] NodePtr Merge(NodePtr left, NodePtr right);
        [[nodiscard]] std::pair<NodePtr, NodePtr> Split(NodePtr root, std::size_t offset);
        void AppendText(const NodePtr& node, std::u16string& output) const;
        void AppendTextRange(const NodePtr& node, std::size_t baseOffset, std::size_t start, std::size_t end, std::u16string& output) const;
        [[nodiscard]] std::uint64_t NextPriority() noexcept;

        std::u16string originalBuffer_{};
        std::u16string addBuffer_{};
        NodePtr root_{};
        std::uint64_t nodeSequence_{1};
        std::vector<std::size_t> originalLineFeeds_{};
        std::vector<std::size_t> addLineFeeds_{};
    };
}
