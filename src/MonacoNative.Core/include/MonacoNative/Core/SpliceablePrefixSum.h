#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <utility>
#include <vector>

namespace MonacoNative::Core
{
    // Implicit-treap prefix-sum sequence. Monaco's projected view model keeps one
    // output-line count per model line and splices those counts when model lines are
    // inserted/deleted. This container provides the same important complexity shape:
    // point update, prefix query and sequence splice are O(log n) expected time.
    template <typename T>
    class SpliceablePrefixSum final
    {
    public:
        SpliceablePrefixSum() = default;

        explicit SpliceablePrefixSum(const std::vector<T>& values)
        {
            Reset(values);
        }

        void Reset(const std::vector<T>& values)
        {
            root_.reset();
            nextSerial_ = 1;
            for (const auto& value : values)
            {
                root_ = Merge(std::move(root_), MakeNode(value));
            }
        }

        [[nodiscard]] std::size_t Size() const noexcept
        {
            return NodeSize(root_);
        }

        [[nodiscard]] T Total() const noexcept
        {
            return NodeSum(root_);
        }

        [[nodiscard]] T PrefixSum(std::size_t count) const
        {
            if (count > Size())
            {
                throw std::out_of_range("Prefix count exceeds sequence size.");
            }
            return PrefixSum(root_.get(), count);
        }

        [[nodiscard]] T GetValue(std::size_t index) const
        {
            if (index >= Size())
            {
                throw std::out_of_range("Prefix-sum index is invalid.");
            }
            const auto* node = root_.get();
            auto remaining = index;
            while (node)
            {
                const auto leftSize = NodeSize(node->left);
                if (remaining < leftSize)
                {
                    node = node->left.get();
                }
                else if (remaining == leftSize)
                {
                    return node->value;
                }
                else
                {
                    remaining -= leftSize + 1;
                    node = node->right.get();
                }
            }
            throw std::logic_error("Prefix-sum tree lookup failed.");
        }

        void SetValue(std::size_t index, T value)
        {
            if (index >= Size())
            {
                throw std::out_of_range("Prefix-sum index is invalid.");
            }
            SetValue(root_.get(), index, value);
        }

        void Splice(std::size_t index, std::size_t removeCount, const std::vector<T>& inserted)
        {
            if (index > Size() || removeCount > Size() - index)
            {
                throw std::out_of_range("Prefix-sum splice range is invalid.");
            }

            auto [left, tail] = Split(std::move(root_), index);
            auto [discarded, right] = Split(std::move(tail), removeCount);
            discarded.reset();

            std::unique_ptr<Node> middle;
            for (const auto& value : inserted)
            {
                middle = Merge(std::move(middle), MakeNode(value));
            }
            root_ = Merge(Merge(std::move(left), std::move(middle)), std::move(right));
        }

        // Returns the zero-based value index containing a cumulative offset. Values
        // must be non-negative and offset must satisfy 0 <= offset < Total(). Zero
        // values are skipped. This mirrors Monaco's prefix-sum index lookup used for
        // view-line -> model-line conversion.
        [[nodiscard]] std::size_t FindIndexByCumulativeOffset(T offset) const
        {
            const auto total = Total();
            if (offset < T{} || !(offset < total))
            {
                throw std::out_of_range("Cumulative offset is outside the prefix-sum total.");
            }

            const auto* node = root_.get();
            std::size_t baseIndex = 0;
            auto remaining = offset;
            while (node)
            {
                const auto leftSum = NodeSum(node->left);
                const auto leftSize = NodeSize(node->left);
                if (remaining < leftSum)
                {
                    node = node->left.get();
                    continue;
                }

                remaining -= leftSum;
                if (T{} < node->value && remaining < node->value)
                {
                    return baseIndex + leftSize;
                }

                remaining -= node->value;
                baseIndex += leftSize + 1;
                node = node->right.get();
            }
            throw std::logic_error("Cumulative prefix-sum lookup failed.");
        }

    private:
        struct Node final
        {
            T value{};
            T sum{};
            std::size_t size{1};
            std::uint32_t priority{};
            std::unique_ptr<Node> left{};
            std::unique_ptr<Node> right{};
        };

        [[nodiscard]] static std::size_t NodeSize(const std::unique_ptr<Node>& node) noexcept
        {
            return node ? node->size : 0;
        }

        [[nodiscard]] static T NodeSum(const std::unique_ptr<Node>& node) noexcept
        {
            return node ? node->sum : T{};
        }

        static void Refresh(Node& node) noexcept
        {
            node.size = NodeSize(node.left) + 1 + NodeSize(node.right);
            node.sum = NodeSum(node.left) + node.value + NodeSum(node.right);
        }

        [[nodiscard]] std::unique_ptr<Node> MakeNode(T value)
        {
            auto node = std::make_unique<Node>();
            node->value = value;
            node->sum = value;
            node->priority = PriorityFor(nextSerial_++);
            return node;
        }

        [[nodiscard]] static std::uint32_t PriorityFor(std::uint64_t serial) noexcept
        {
            // Deterministic SplitMix64 finalizer: stable tests without relying on a
            // global RNG while still giving treap-quality priorities.
            auto x = serial + 0x9E3779B97F4A7C15ULL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
            x ^= x >> 31;
            return static_cast<std::uint32_t>(x ^ (x >> 32));
        }

        [[nodiscard]] static std::unique_ptr<Node> Merge(std::unique_ptr<Node> left, std::unique_ptr<Node> right)
        {
            if (!left)
            {
                return right;
            }
            if (!right)
            {
                return left;
            }
            if (left->priority >= right->priority)
            {
                left->right = Merge(std::move(left->right), std::move(right));
                Refresh(*left);
                return left;
            }
            right->left = Merge(std::move(left), std::move(right->left));
            Refresh(*right);
            return right;
        }

        [[nodiscard]] static std::pair<std::unique_ptr<Node>, std::unique_ptr<Node>> Split(
            std::unique_ptr<Node> root,
            std::size_t leftCount)
        {
            if (!root)
            {
                return {};
            }

            const auto leftSize = NodeSize(root->left);
            if (leftCount <= leftSize)
            {
                auto [left, middle] = Split(std::move(root->left), leftCount);
                root->left = std::move(middle);
                Refresh(*root);
                return {std::move(left), std::move(root)};
            }

            auto [middle, right] = Split(std::move(root->right), leftCount - leftSize - 1);
            root->right = std::move(middle);
            Refresh(*root);
            return {std::move(root), std::move(right)};
        }

        [[nodiscard]] static T PrefixSum(const Node* node, std::size_t count) noexcept
        {
            if (!node || count == 0)
            {
                return T{};
            }
            const auto leftSize = node->left ? node->left->size : 0;
            if (count <= leftSize)
            {
                return PrefixSum(node->left.get(), count);
            }

            auto result = node->left ? node->left->sum : T{};
            if (count == leftSize + 1)
            {
                return result + node->value;
            }
            result += node->value;
            return result + PrefixSum(node->right.get(), count - leftSize - 1);
        }

        static void SetValue(Node* node, std::size_t index, T value)
        {
            const auto leftSize = node->left ? node->left->size : 0;
            if (index < leftSize)
            {
                SetValue(node->left.get(), index, value);
            }
            else if (index == leftSize)
            {
                node->value = value;
            }
            else
            {
                SetValue(node->right.get(), index - leftSize - 1, value);
            }
            Refresh(*node);
        }

        std::unique_ptr<Node> root_{};
        std::uint64_t nextSerial_{1};
    };
}
