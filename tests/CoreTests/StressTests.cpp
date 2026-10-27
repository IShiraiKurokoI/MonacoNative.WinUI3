#include "TestHarness.h"
#include "MonacoNative/Core/TextModel.h"

#include <cstddef>
#include <cstdint>
#include <string>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    std::u16string MakeDocument(std::int32_t lineCount)
    {
        std::u16string text;
        text.reserve(static_cast<std::size_t>(lineCount) * 24);
        for (std::int32_t line = 0; line < lineCount; ++line)
        {
            if (line > 0) text.push_back(u'\n');
            text += u"    value = value + 1;";
        }
        return text;
    }

    void LargeDocumentRepeatedEditUndo()
    {
        constexpr std::int32_t lineCount = 100'000;
        TextModel model(MakeDocument(lineCount), "cpp");
        MN_CHECK_EQ(model.GetLineCount(), static_cast<std::size_t>(lineCount));
        for (std::int32_t iteration = 0; iteration < 72; ++iteration)
        {
            const auto line = 1 + (iteration * 1'391) % lineCount;
            const auto offset = model.GetOffsetAt({line, 5});
            const auto original = model.GetLineContent(line);
            model.ApplyOffsetEdits({OffsetEdit{offset, offset, u"\U0001F4BB"}});
            MN_CHECK_EQ(model.GetLineCount(), static_cast<std::size_t>(lineCount));
            MN_CHECK_EQ(model.GetLineContent(line).size(), original.size() + 2);
            MN_CHECK(model.Undo());
            MN_CHECK_EQ(model.GetLineContent(line), original);
            MN_CHECK(model.Redo());
            MN_CHECK(model.Undo());
        }
        MN_CHECK_EQ(model.GetValue(), MakeDocument(lineCount));
    }

    void LongLineAndSoftWrapInput()
    {
        std::u16string text(200'000, u'X');
        text += u"\nshort\n";
        TextModel model(text, "plaintext");
        MN_CHECK_EQ(model.GetLineCount(), 3U);
        MN_CHECK_EQ(model.GetOffsetAt({1, 200'001}), 200'000U);
        MN_CHECK_EQ(model.GetPositionAt(200'001U), (Position{2, 1}));
        model.ApplyOffsetEdits({OffsetEdit{199'999U, 200'000U, u"Z"}});
        MN_CHECK_EQ(model.GetLineContent(1).back(), u'Z');
        MN_CHECK(model.Undo());
        MN_CHECK_EQ(model.GetLineContent(1).back(), u'X');
    }

    void RepeatedModelLifetime()
    {
        const auto baseline = MakeDocument(12'000);
        for (int iteration = 0; iteration < 32; ++iteration)
        {
            TextModel model(baseline, "typescript");
            MN_CHECK_EQ(model.GetLineCount(), 12'000U);
            model.ApplyOffsetEdits({OffsetEdit{0, 0, u"// start\n"}});
            MN_CHECK_EQ(model.GetLineCount(), 12'001U);
            MN_CHECK(model.Undo());
            MN_CHECK_EQ(model.GetLineCount(), 12'000U);
            MN_CHECK_EQ(model.GetLineContent(1), std::u16string(u"    value = value + 1;"));
        }
    }
}

int main()
{
    Suite suite("CoreStressTests");
    suite.Run("100k line edit/undo/redo with supplementary Unicode", LargeDocumentRepeatedEditUndo);
    suite.Run("200k character line edit and position mapping", LongLineAndSoftWrapInput);
    suite.Run("repeated native text-model creation and disposal", RepeatedModelLifetime);
    return suite.Finish();
}
