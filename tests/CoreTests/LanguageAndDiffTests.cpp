#include "TestHarness.h"
#include "MonacoNative/Core/DiffEngine.h"
#include "MonacoNative/Core/Tokenizer.h"

#include <algorithm>
#include <cstdint>
#include <random>
#include <stdexcept>
#include <string>
#include <vector>

using namespace MonacoNative::Core;
using MonacoNative::Tests::Suite;

namespace
{
    void CppTokenizerCoverage()
    {
        CppTokenizer tokenizer;
        const auto tokens = tokenizer.TokenizeLine(u"const int answer = 0x2A; \"hi\\\"x\" // comment");
        MN_CHECK(tokens.size() >= 8U);
        MN_CHECK_EQ(tokens[0].kind, TokenKind::Keyword);
        MN_CHECK_EQ(tokens[1].kind, TokenKind::Keyword);
        MN_CHECK_EQ(tokens[2].kind, TokenKind::Identifier);
        bool sawNumber = false;
        bool sawString = false;
        bool sawComment = false;
        for (const auto& token : tokens)
        {
            sawNumber = sawNumber || token.kind == TokenKind::Number;
            sawString = sawString || token.kind == TokenKind::String;
            sawComment = sawComment || token.kind == TokenKind::Comment;
            MN_CHECK(token.startColumn >= 1);
            MN_CHECK(token.endColumn > token.startColumn);
        }
        MN_CHECK(sawNumber && sawString && sawComment);
    }

    void JsonTokenizerCoverage()
    {
        JsonTokenizer tokenizer;
        const auto tokens = tokenizer.TokenizeLine(u"{\"value\":-1.25e+2,\"ok\":true}");
        MN_CHECK(!tokens.empty());
        bool sawNumber = false;
        bool sawKeyword = false;
        int strings = 0;
        for (const auto& token : tokens)
        {
            sawNumber = sawNumber || token.kind == TokenKind::Number;
            sawKeyword = sawKeyword || token.kind == TokenKind::Keyword;
            strings += token.kind == TokenKind::String ? 1 : 0;
        }
        MN_CHECK(sawNumber);
        MN_CHECK(sawKeyword);
        MN_CHECK_EQ(strings, 2);
    }

    void TokenizerFactory()
    {
        MN_CHECK(CreateBuiltinTokenizer("cpp") != nullptr);
        MN_CHECK(CreateBuiltinTokenizer("c") != nullptr);
        MN_CHECK(CreateBuiltinTokenizer("c++") != nullptr);
        MN_CHECK(CreateBuiltinTokenizer("json") != nullptr);
        MN_CHECK(CreateBuiltinTokenizer("jsonc") != nullptr);
        MN_CHECK(CreateBuiltinTokenizer("plaintext") == nullptr);
    }

    void DiffIdenticalAndReplace()
    {
        auto diff = DiffEngine::ComputeLineDiff(u"a\nb\nc", u"a\nb\nc");
        MN_CHECK_EQ(diff.size(), 3U);
        for (const auto& line : diff)
        {
            MN_CHECK_EQ(line.kind, DiffKind::Equal);
        }

        diff = DiffEngine::ComputeLineDiff(u"a\nb\nc", u"a\nx\nc");
        bool sawInsert = false;
        bool sawDelete = false;
        for (const auto& line : diff)
        {
            sawInsert = sawInsert || line.kind == DiffKind::Insert;
            sawDelete = sawDelete || line.kind == DiffKind::Delete;
        }
        MN_CHECK(sawInsert && sawDelete);
    }

    void DiffInsertDeleteAndCrlf()
    {
        auto diff = DiffEngine::ComputeLineDiff(u"a\r\nb", u"a\r\nb\r\nc");
        MN_CHECK_EQ(diff.size(), 3U);
        MN_CHECK_EQ(diff[0].kind, DiffKind::Equal);
        MN_CHECK_EQ(diff[1].kind, DiffKind::Equal);
        MN_CHECK_EQ(diff[2].kind, DiffKind::Insert);
        MN_CHECK_EQ(diff[2].text, std::u16string(u"c"));

        diff = DiffEngine::ComputeLineDiff(u"a\nb\nc", u"a\nc");
        MN_CHECK_EQ(diff.size(), 3U);
        MN_CHECK_EQ(diff[1].kind, DiffKind::Delete);
        MN_CHECK_EQ(diff[1].text, std::u16string(u"b"));
    }

    void DiffAdvancedLargeInput()
    {
        std::u16string left;
        std::u16string right;
        for (int i = 0; i < 3000; ++i)
        {
            left += u"a\n";
            right += u"b\n";
        }
        const auto result = DiffEngine::ComputeDiff(left, right, DiffOptions{false, true});
        MN_CHECK(!result.changes.empty());
        MN_CHECK_EQ(result.changes.front().originalStartLine, 1);
        MN_CHECK_EQ(result.changes.front().modifiedStartLine, 1);
        MN_CHECK(result.lines.size() >= 6000U);
    }

    void DiffInnerChanges()
    {
        const auto result = DiffEngine::ComputeDiff(u"alpha beta", u"alpha BETA", DiffOptions{false, false});
        MN_CHECK_EQ(result.changes.size(), 1U);
        MN_CHECK(!result.changes[0].innerChanges.empty());
        const auto& inner = result.changes[0].innerChanges.front();
        MN_CHECK_EQ(inner.originalLine, 1);
        MN_CHECK_EQ(inner.modifiedLine, 1);
        MN_CHECK(inner.originalStartColumn >= 6);
        MN_CHECK(inner.originalEndColumn > inner.originalStartColumn);
        MN_CHECK(inner.modifiedEndColumn > inner.modifiedStartColumn);
    }

    void DiffMovedBlockDetection()
    {
        const auto result = DiffEngine::ComputeDiff(
            u"head\na1\na2\nb1\nb2\nend",
            u"head\nb1\nb2\na1\na2\nend",
            DiffOptions{false, true});
        MN_CHECK(!result.moves.empty());
        const auto& move = result.moves.front();
        MN_CHECK_EQ(move.originalEndLineExclusive - move.originalStartLine,
                    move.modifiedEndLineExclusive - move.modifiedStartLine);
        MN_CHECK(move.originalEndLineExclusive - move.originalStartLine >= 2);
    }


    void DiffMyersRandomizedParity()
    {
        std::mt19937 rng(0xD1FF2026U);
        const std::vector<std::u16string> alphabet{u"a", u"b", u"c", u"d"};

        auto join = [](const std::vector<std::u16string>& lines)
        {
            std::u16string text;
            for (std::size_t i = 0; i < lines.size(); ++i)
            {
                if (i != 0) text += u'\n';
                text += lines[i];
            }
            return text;
        };

        auto lcsLength = [](const std::vector<std::u16string>& left, const std::vector<std::u16string>& right)
        {
            std::vector<std::size_t> previous(right.size() + 1, 0);
            std::vector<std::size_t> current(right.size() + 1, 0);
            for (const auto& leftLine : left)
            {
                std::fill(current.begin(), current.end(), 0);
                for (std::size_t j = 1; j <= right.size(); ++j)
                {
                    current[j] = leftLine == right[j - 1]
                        ? previous[j - 1] + 1
                        : std::max(previous[j], current[j - 1]);
                }
                std::swap(previous, current);
            }
            return previous.back();
        };

        for (int iteration = 0; iteration < 500; ++iteration)
        {
            const auto leftCount = static_cast<std::size_t>(rng() % 9U);
            const auto rightCount = static_cast<std::size_t>(rng() % 9U);
            std::vector<std::u16string> left;
            std::vector<std::u16string> right;
            left.reserve(leftCount);
            right.reserve(rightCount);
            for (std::size_t i = 0; i < leftCount; ++i) left.push_back(alphabet[rng() % alphabet.size()]);
            for (std::size_t i = 0; i < rightCount; ++i) right.push_back(alphabet[rng() % alphabet.size()]);

            // SplitLines intentionally represents an empty document as one empty line.
            // Keep this property test focused on non-empty line sequences.
            if (left.empty()) left.push_back(u"");
            if (right.empty()) right.push_back(u"");

            const auto diff = DiffEngine::ComputeLineDiff(join(left), join(right));
            std::vector<std::u16string> rebuiltLeft;
            std::vector<std::u16string> rebuiltRight;
            std::size_t edits = 0;
            for (const auto& line : diff)
            {
                if (line.kind != DiffKind::Insert) rebuiltLeft.push_back(line.text);
                if (line.kind != DiffKind::Delete) rebuiltRight.push_back(line.text);
                edits += line.kind == DiffKind::Equal ? 0U : 1U;
            }
            MN_CHECK_EQ(rebuiltLeft, left);
            MN_CHECK_EQ(rebuiltRight, right);
            const auto lcs = lcsLength(left, right);
            MN_CHECK_EQ(edits, left.size() + right.size() - (2U * lcs));
        }
    }

    void DiffIgnoreTrimWhitespace()
    {
        const auto ignored = DiffEngine::ComputeDiff(u"  value  \nnext", u"value\nnext", DiffOptions{true, false});
        MN_CHECK(ignored.changes.empty());
        const auto strict = DiffEngine::ComputeDiff(u"  value  \nnext", u"value\nnext", DiffOptions{false, false});
        MN_CHECK_EQ(strict.changes.size(), 1U);
    }
}

int main()
{
    Suite suite("LanguageAndDiffTests");
    suite.Run("C++ tokenizer", CppTokenizerCoverage);
    suite.Run("JSON tokenizer", JsonTokenizerCoverage);
    suite.Run("tokenizer factory", TokenizerFactory);
    suite.Run("diff identical and replace", DiffIdenticalAndReplace);
    suite.Run("diff insert/delete and CRLF", DiffInsertDeleteAndCrlf);
    suite.Run("diff advanced large input", DiffAdvancedLargeInput);
    suite.Run("diff inner changes", DiffInnerChanges);
    suite.Run("diff moved block detection", DiffMovedBlockDetection);
    suite.Run("diff Myers randomized parity", DiffMyersRandomizedParity);
    suite.Run("diff ignore trim whitespace", DiffIgnoreTrimWhitespace);
    return suite.Finish();
}
