#include "TestHarness.h"
#include "MonacoNative/Core/FoldingModel.h"
#include "MonacoNative/Core/ViewModel.h"

#include <memory>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void IndentationProviderBuildsNestedRanges()
    {
        auto model = std::make_shared<TextModel>(u"root\n  child\n    grand\n  child2\ntail");
        IndentationFoldingRangeProvider provider(2);
        const auto ranges = provider.Compute(*model);
        MN_CHECK_EQ(ranges.size(), 2U);
        MN_CHECK_EQ(ranges[0], (FoldingRangeCandidate{1, 4, "indent"}));
        MN_CHECK_EQ(ranges[1], (FoldingRangeCandidate{2, 3, "indent"}));
    }

    void CollapseProducesMergedHiddenAreas()
    {
        auto model = std::make_shared<TextModel>(u"1\n2\n3\n4\n5\n6");
        FoldingModel folding(model);
        folding.SetRanges({{1, 5, "region"}, {2, 3, "region"}, {5, 6, "region"}});
        MN_CHECK_EQ(folding.Ranges().size(), 3U);
        const auto parent = folding.Ranges()[0].id;
        const auto tail = folding.Ranges()[2].id;
        MN_CHECK(folding.SetCollapsed(parent, true));
        MN_CHECK(folding.SetCollapsed(tail, true));
        const auto hidden = folding.HiddenAreas();
        MN_CHECK_EQ(hidden.size(), 1U);
        MN_CHECK_EQ(hidden[0], (FoldingHiddenArea{2, 6}));
        MN_CHECK(!folding.SetCollapsed(999999, true));
    }

    void ProviderRefreshPreservesCollapseState()
    {
        auto model = std::make_shared<TextModel>(u"root\n  a\n  b\ntail");
        IndentationFoldingRangeProvider provider(2);
        FoldingModel folding(model);
        folding.UpdateFromProvider(provider);
        MN_CHECK_EQ(folding.Ranges().size(), 1U);
        const auto id = folding.Ranges()[0].id;
        MN_CHECK(folding.SetCollapsed(id, true));
        folding.UpdateFromProvider(provider);
        MN_CHECK_EQ(folding.Ranges().size(), 1U);
        MN_CHECK_EQ(folding.Ranges()[0].id, id);
        MN_CHECK(folding.Ranges()[0].collapsed);
    }

    void RangesTrackDocumentEdits()
    {
        auto model = std::make_shared<TextModel>(u"a\nb\nc\nd\ne");
        FoldingModel folding(model);
        folding.SetRanges({{2, 4, "manual"}});
        const auto id = folding.Ranges()[0].id;
        MN_CHECK(folding.SetCollapsed(id, true));

        model->ApplyOffsetEdits({OffsetEdit{0, 0, u"x\n"}});
        MN_CHECK_EQ(folding.Ranges().size(), 1U);
        MN_CHECK_EQ(folding.Ranges()[0].startLineNumber, 3);
        MN_CHECK_EQ(folding.Ranges()[0].endLineNumber, 5);
        MN_CHECK(folding.Ranges()[0].collapsed);

        MN_CHECK(model->Undo());
        MN_CHECK_EQ(folding.Ranges()[0].startLineNumber, 2);
        MN_CHECK_EQ(folding.Ranges()[0].endLineNumber, 4);
    }

    void ViewModelReactsToFoldingState()
    {
        auto model = std::make_shared<TextModel>(u"root\nchild1\nchild2\ntail");
        auto folding = std::make_shared<FoldingModel>(model);
        folding->SetRanges({{1, 3, "manual"}});
        ViewModel view(model);
        view.SetFoldingModel(folding);
        MN_CHECK_EQ(view.ViewLineCount(), 4);

        const auto id = folding->Ranges()[0].id;
        MN_CHECK(folding->SetCollapsed(id, true));
        MN_CHECK_EQ(view.ViewLineCount(), 2);
        MN_CHECK_EQ(view.GetViewLine(1).modelLineNumber, 1);
        MN_CHECK_EQ(view.GetViewLine(2).modelLineNumber, 4);
        MN_CHECK_EQ(view.HiddenAreas(), (std::vector<HiddenArea>{{2, 3}}));

        MN_CHECK(folding->SetCollapsed(id, false));
        MN_CHECK_EQ(view.ViewLineCount(), 4);
    }

    void RecursiveCollapseTargetsNestedRanges()
    {
        auto model = std::make_shared<TextModel>(u"1\n2\n3\n4\n5");
        FoldingModel folding(model);
        folding.SetRanges({{1, 5, "outer"}, {2, 4, "inner"}, {3, 4, "inner2"}});
        MN_CHECK_EQ(folding.SetCollapsedAtLine(1, true, true), 3U);
        for (const auto& range : folding.Ranges())
        {
            MN_CHECK(range.collapsed);
        }
        folding.ExpandAll();
        for (const auto& range : folding.Ranges())
        {
            MN_CHECK(!range.collapsed);
        }
        folding.CollapseAll();
        for (const auto& range : folding.Ranges())
        {
            MN_CHECK(range.collapsed);
        }
    }
}

int main()
{
    Suite suite("FoldingModelTests");
    suite.Run("indentation provider", IndentationProviderBuildsNestedRanges);
    suite.Run("hidden area merge", CollapseProducesMergedHiddenAreas);
    suite.Run("provider state preservation", ProviderRefreshPreservesCollapseState);
    suite.Run("range edit tracking", RangesTrackDocumentEdits);
    suite.Run("view-model folding integration", ViewModelReactsToFoldingState);
    suite.Run("recursive collapse", RecursiveCollapseTargetsNestedRanges);
    return suite.Finish();
}
