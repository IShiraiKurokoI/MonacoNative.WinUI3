#include "TestHarness.h"
#include "MonacoNative/Core/CursorModel.h"

#include <memory>
#include <string>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void BasicNavigationAndSelection()
    {
        auto model = std::make_shared<TextModel>(u"abc\ndef", "plaintext");
        CursorModel cursor(model);
        cursor.MoveRight();
        cursor.MoveRight(true);
        MN_CHECK_EQ(cursor.PrimarySelection().anchor, (Position{1, 2}));
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 3}));
        cursor.MoveRight(false);
        MN_CHECK(cursor.PrimarySelection().IsEmpty());
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 3}));
        cursor.MoveEnd();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 4}));
        cursor.MoveDown();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{2, 4}));
        cursor.MoveHome();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{2, 1}));
    }

    void PreferredVerticalColumn()
    {
        auto model = std::make_shared<TextModel>(u"123456789\nx\n123456789", "plaintext");
        CursorModel cursor(model);
        cursor.SetPrimarySelection(Selection{Position{1, 8}, Position{1, 8}});
        cursor.MoveDown();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{2, 2}));
        cursor.MoveDown();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{3, 8}));
        cursor.MoveLeft();
        cursor.MoveUp();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{2, 2}));
        cursor.MoveUp();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 7}));
    }

    void GraphemeNavigation()
    {
        {
            auto model = std::make_shared<TextModel>(u"e\u0301x");
            CursorModel cursor(model);
            cursor.MoveRight();
            MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 3}));
            cursor.Backspace();
            MN_CHECK_EQ(model->GetValue(), std::u16string(u"x"));
        }
        {
            auto model = std::make_shared<TextModel>(u"\u2764\uFE0Fx");
            CursorModel cursor(model);
            cursor.MoveRight();
            MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 3}));
        }
        {
            auto model = std::make_shared<TextModel>(u"\U0001F44D\U0001F3FDx");
            CursorModel cursor(model);
            cursor.MoveRight();
            MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 5}));
        }
        {
            auto model = std::make_shared<TextModel>(u"\U0001F469\u200D\U0001F4BBx");
            CursorModel cursor(model);
            cursor.MoveRight();
            MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 6}));
            cursor.Backspace();
            MN_CHECK_EQ(model->GetValue(), std::u16string(u"x"));
        }
        {
            auto model = std::make_shared<TextModel>(u"\U0001F1FA\U0001F1F8!");
            CursorModel cursor(model);
            cursor.MoveRight();
            MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 5}));
        }
    }

    void CrlfIsSingleNavigationUnit()
    {
        auto model = std::make_shared<TextModel>(u"a\r\nb");
        CursorModel cursor(model);
        cursor.MoveRight();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 2}));
        cursor.MoveRight();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{2, 1}));
        cursor.Backspace();
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"ab"));
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 2}));
    }

    void WordNavigationAndDeletion()
    {
        auto model = std::make_shared<TextModel>(u"one  two...three");
        CursorModel cursor(model);
        cursor.MoveWordRight();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 6}));
        cursor.MoveWordRight();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 9}));
        cursor.MoveWordRight();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 12}));
        cursor.MoveWordLeft();
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 9}));

        cursor.SetPrimarySelection(Selection{Position{1, 17}, Position{1, 17}});
        cursor.DeleteWordLeft();
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"one  two..."));
        cursor.SetPrimarySelection(Selection{Position{1, 1}, Position{1, 1}});
        cursor.DeleteWordRight();
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"two..."));
    }

    void MultiCursorEditing()
    {
        auto model = std::make_shared<TextModel>(u"abcdef");
        CursorModel cursor(model);
        cursor.SetSelections({
            Selection{Position{1, 3}, Position{1, 3}},
            Selection{Position{1, 6}, Position{1, 6}},
            Selection{Position{1, 6}, Position{1, 6}},
        });
        MN_CHECK_EQ(cursor.Selections().size(), 2U);
        cursor.Backspace();
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"acdf"));
        MN_CHECK_EQ(cursor.Selections().size(), 2U);
        cursor.InsertText(u"X");
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"aXcdXf"));
        MN_CHECK(model->Undo());
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"acdf"));
    }

    void OverlappingSelectionsMergeForEditing()
    {
        auto model = std::make_shared<TextModel>(u"abcdefgh");
        CursorModel cursor(model);
        cursor.SetSelections({
            Selection{Position{1, 2}, Position{1, 5}},
            Selection{Position{1, 4}, Position{1, 7}},
        });
        MN_CHECK_EQ(cursor.Selections().size(), 1U);
        cursor.InsertText(u"X");
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"aXgh"));
    }

    void SelectionReplacement()
    {
        auto model = std::make_shared<TextModel>(u"hello world");
        CursorModel cursor(model);
        cursor.SetPrimarySelection(Selection{Position{1, 7}, Position{1, 12}});
        cursor.InsertText(u"WinUI");
        MN_CHECK_EQ(model->GetValue(), std::u16string(u"hello WinUI"));
        MN_CHECK_EQ(cursor.PrimarySelection().active, (Position{1, 12}));
    }
}

int main()
{
    Suite suite("CursorTests");
    suite.Run("basic navigation and selection", BasicNavigationAndSelection);
    suite.Run("preferred vertical column", PreferredVerticalColumn);
    suite.Run("grapheme navigation", GraphemeNavigation);
    suite.Run("CRLF navigation", CrlfIsSingleNavigationUnit);
    suite.Run("word navigation and deletion", WordNavigationAndDeletion);
    suite.Run("multi-cursor editing", MultiCursorEditing);
    suite.Run("overlapping selections merge", OverlappingSelectionsMergeForEditing);
    suite.Run("selection replacement", SelectionReplacement);
    return suite.Finish();
}
