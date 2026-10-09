#include "TestHarness.h"
#include "MonacoNative/Core/Decorations.h"
#include "MonacoNative/Core/Event.h"
#include "MonacoNative/Core/TextModel.h"
#include "MonacoNative/Core/SpliceablePrefixSum.h"
#include "MonacoNative/Core/UndoRedoService.h"

#include <atomic>
#include <random>
#include <numeric>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void EventSourceSubscription()
    {
        EventSource<int> source;
        int sum = 0;
        const auto first = source.Add([&](const int& value) { sum += value; });
        const auto second = source.Add([&](const int& value) { sum += value * 10; });
        MN_CHECK(first != second);
        source.Emit(2);
        MN_CHECK_EQ(sum, 22);
        source.Remove(first);
        source.Emit(1);
        MN_CHECK_EQ(sum, 32);
        source.Remove(second);
        source.Emit(99);
        MN_CHECK_EQ(sum, 32);
    }

    void EventSourceSnapshotAllowsMutationDuringEmit()
    {
        EventSource<int> source;
        EventSource<int>::Token self{};
        int calls = 0;
        self = source.Add([&](const int&)
        {
            ++calls;
            source.Remove(self);
        });
        const auto other = source.Add([&](const int&) { ++calls; });
        MN_CHECK(other != 0);
        source.Emit(1);
        MN_CHECK_EQ(calls, 2);
        source.Emit(1);
        MN_CHECK_EQ(calls, 3);
    }

    void EventSourceConcurrentEmit()
    {
        EventSource<int> source;
        std::atomic<int> total{0};
        const auto token = source.Add([&](const int& value) { total.fetch_add(value, std::memory_order_relaxed); });
        MN_CHECK(token != 0);
        std::vector<std::thread> threads;
        for (int i = 0; i < 4; ++i)
        {
            threads.emplace_back([&]()
            {
                for (int j = 0; j < 500; ++j)
                {
                    source.Emit(1);
                }
            });
        }
        for (auto& thread : threads)
        {
            thread.join();
        }
        MN_CHECK_EQ(total.load(), 2000);
    }

    void UndoRedoServiceStacks()
    {
        UndoRedoService service;
        MN_CHECK(!service.CanUndo());
        MN_CHECK(!service.CanRedo());
        MN_CHECK_THROWS((void)service.PopUndo(), std::logic_error);
        MN_CHECK_THROWS((void)service.PopRedo(), std::logic_error);

        EditBatch first{{OffsetEdit{0, 0, u"a"}}, {OffsetEdit{0, 1, u""}}};
        service.Push(first);
        MN_CHECK(service.CanUndo());
        auto popped = service.PopUndo();
        MN_CHECK_EQ(popped.steps.size(), 1U);
        MN_CHECK_EQ(popped.steps.front().forward.size(), 1U);
        service.PushRedo(popped);
        MN_CHECK(service.CanRedo());
        popped = service.PopRedo();
        service.PushUndoFromRedo(popped);
        MN_CHECK(service.CanUndo());

        service.Push(EditBatch{{OffsetEdit{1, 1, u"b"}}, {OffsetEdit{1, 2, u""}}});
        MN_CHECK(!service.CanRedo());
        service.Clear();
        MN_CHECK(!service.CanUndo());
    }

    void SpliceablePrefixSumSupportsLocalSplices()
    {
        SpliceablePrefixSum<std::int64_t> values({2, 0, 3, 1});
        MN_CHECK_EQ(values.Size(), 4U);
        MN_CHECK_EQ(values.Total(), 6);
        MN_CHECK_EQ(values.PrefixSum(0), 0);
        MN_CHECK_EQ(values.PrefixSum(2), 2);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(0), 0U);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(1), 0U);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(2), 2U);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(4), 2U);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(5), 3U);

        values.SetValue(1, 4);
        MN_CHECK_EQ(values.Total(), 10);
        MN_CHECK_EQ(values.PrefixSum(2), 6);
        values.Splice(1, 2, std::vector<std::int64_t>{1, 2, 0});
        MN_CHECK_EQ(values.Size(), 5U);
        MN_CHECK_EQ(values.GetValue(0), 2);
        MN_CHECK_EQ(values.GetValue(1), 1);
        MN_CHECK_EQ(values.GetValue(2), 2);
        MN_CHECK_EQ(values.GetValue(3), 0);
        MN_CHECK_EQ(values.GetValue(4), 1);
        MN_CHECK_EQ(values.Total(), 6);
        MN_CHECK_EQ(values.FindIndexByCumulativeOffset(5), 4U);
        MN_CHECK_THROWS((void)values.FindIndexByCumulativeOffset(6), std::out_of_range);
    }

    void PrefixSumBulkRebuildIsEquivalentToSequentialUpdates()
    {
        std::mt19937 rng(9917);
        SpliceablePrefixSum<std::int64_t> sequence;
        for (const auto count : {0U, 1U, 15U, 1024U, 10000U})
        {
            std::vector<std::int64_t> values(count);
            std::int64_t total = 0;
            for (auto& value : values)
            {
                value = rng() % 8;
                total += value;
            }
            sequence.Reset(values);
            MN_CHECK_EQ(sequence.Values(), values);
            MN_CHECK_EQ(sequence.Total(), total);
            MN_CHECK_EQ(sequence.Size(), values.size());
            for (int trial = 0; trial < 50; ++trial)
            {
                const auto prefixSize = count ? static_cast<std::size_t>(rng()) % (count + 1) : 0U;
                const auto expectedPrefix = std::accumulate(values.begin(), values.begin() + prefixSize, std::int64_t{});
                MN_CHECK_EQ(sequence.PrefixSum(prefixSize), expectedPrefix);
                if (total > 0)
                {
                    const auto offset = static_cast<std::int64_t>(rng()) % total;
                    const auto index = sequence.FindIndexByCumulativeOffset(offset);
                    MN_CHECK(sequence.PrefixSum(index) <= offset);
                    MN_CHECK(sequence.PrefixSum(index + 1) > offset);
                }
            }
        }
    }

    void DecorationCollectionBasics()
    {
        DecorationCollection decorations;
        const auto a = decorations.Add(Range{1, 1, 1, 3});
        DecorationOptions options{};
        options.className = "warning";
        options.isWholeLine = true;
        const auto b = decorations.Add(Range{2, 1, 3, 1}, options);
        MN_CHECK(a != b);
        MN_CHECK_EQ(decorations.All().size(), 2U);
        MN_CHECK_EQ(decorations.QueryLine(1).size(), 1U);
        MN_CHECK_EQ(decorations.QueryLine(2).size(), 1U);
        MN_CHECK_EQ(decorations.QueryLine(3).size(), 1U);
        MN_CHECK(decorations.SetRange(a, Range{4, 1, 4, 2}));
        MN_CHECK_EQ(decorations.Get(a)->range, (Range{4, 1, 4, 2}));
        MN_CHECK(!decorations.SetRange(999, Range{}));
        MN_CHECK(decorations.Remove(b));
        MN_CHECK(!decorations.Remove(b));
        decorations.Clear();
        MN_CHECK(decorations.All().empty());
    }

    Range DecorationAfterInsertion(DecorationStickiness stickiness, std::size_t offset)
    {
        TextModel model(u"abc");
        DecorationOptions options{};
        options.stickiness = stickiness;
        const auto id = model.Decorations().Add(Range{1, 2, 1, 3}, options);
        model.ApplyOffsetEdits({OffsetEdit{offset, offset, u"X"}});
        return model.Decorations().Get(id)->range;
    }

    void DecorationStickinessAtEdges()
    {
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::AlwaysGrowsWhenTypingAtEdges, 1),
            (Range{1, 2, 1, 4}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::NeverGrowsWhenTypingAtEdges, 1),
            (Range{1, 3, 1, 4}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::GrowsOnlyWhenTypingBefore, 1),
            (Range{1, 2, 1, 4}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::GrowsOnlyWhenTypingAfter, 1),
            (Range{1, 3, 1, 4}));

        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::AlwaysGrowsWhenTypingAtEdges, 2),
            (Range{1, 2, 1, 4}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::NeverGrowsWhenTypingAtEdges, 2),
            (Range{1, 2, 1, 3}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::GrowsOnlyWhenTypingBefore, 2),
            (Range{1, 2, 1, 3}));
        MN_CHECK_EQ(
            DecorationAfterInsertion(DecorationStickiness::GrowsOnlyWhenTypingAfter, 2),
            (Range{1, 2, 1, 4}));
    }

    void DecorationsTrackDeletionUndoRedo()
    {
        TextModel model(u"0123456789");
        const auto id = model.Decorations().Add(Range{1, 4, 1, 8});
        model.ApplyOffsetEdits({OffsetEdit{4, 6, u""}});
        MN_CHECK_EQ(model.Decorations().Get(id)->range, (Range{1, 4, 1, 6}));
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.Decorations().Get(id)->range, (Range{1, 4, 1, 8}));
        MN_CHECK(model.Redo());
        MN_CHECK_EQ(model.Decorations().Get(id)->range, (Range{1, 4, 1, 6}));
    }
}

int main()
{
    Suite suite("InfrastructureTests");
    suite.Run("event subscription", EventSourceSubscription);
    suite.Run("event snapshot mutation", EventSourceSnapshotAllowsMutationDuringEmit);
    suite.Run("event concurrent emit", EventSourceConcurrentEmit);
    suite.Run("undo/redo stack service", UndoRedoServiceStacks);
    suite.Run("spliceable prefix sum", SpliceablePrefixSumSupportsLocalSplices);
    suite.Run("bulk prefix sum rebuild", PrefixSumBulkRebuildIsEquivalentToSequentialUpdates);
    suite.Run("decoration collection basics", DecorationCollectionBasics);
    suite.Run("decoration edge stickiness", DecorationStickinessAtEdges);
    suite.Run("decorations track delete/undo/redo", DecorationsTrackDeletionUndoRedo);
    return suite.Finish();
}
