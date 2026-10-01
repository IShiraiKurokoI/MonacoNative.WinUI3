#include "MonacoNative/Core/PieceTreeTextBuffer.h"
#include "MonacoNative/Core/TextModel.h"
#include "MonacoNative/Core/ViewModel.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <random>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

using namespace MonacoNative::Core;

namespace
{
    using Clock = std::chrono::steady_clock;

    struct Profile final
    {
        std::string name;
        std::int32_t lines{};
        std::int32_t lineLength{};
        std::int32_t lookupIterations{};
        std::int32_t typingEdits{};
        std::int32_t randomEdits{};
        std::int32_t mappingIterations{};
    };

    struct Result final
    {
        std::string name;
        std::map<std::string, double> metrics;
        std::map<std::string, std::uint64_t> counts;
        std::uint64_t checksum{};
    };

    class CountingMeasurer final : public ITextMeasurer
    {
    public:
        [[nodiscard]] double Measure(std::u16string_view text) const override
        {
            ++calls;
            return static_cast<double>(text.size());
        }

        mutable std::uint64_t calls{};
    };

    [[nodiscard]] std::u16string MakeDocument(std::int32_t lines, std::int32_t lineLength)
    {
        if (lines <= 0 || lineLength <= 0)
        {
            throw std::invalid_argument("benchmark document dimensions must be positive");
        }

        std::u16string text;
        text.reserve(static_cast<std::size_t>(lines) * static_cast<std::size_t>(lineLength + 1));
        for (std::int32_t line = 0; line < lines; ++line)
        {
            if (line != 0)
            {
                text.push_back(u'\n');
            }
            for (std::int32_t column = 0; column < lineLength; ++column)
            {
                text.push_back(static_cast<char16_t>(u'a' + ((line + column) % 26)));
            }
        }
        return text;
    }

    [[nodiscard]] double Milliseconds(Clock::time_point start, Clock::time_point end)
    {
        return std::chrono::duration<double, std::milli>(end - start).count();
    }

    [[nodiscard]] Result BenchmarkPieceTreePositionLookup(const Profile& profile)
    {
        PieceTreeTextBuffer buffer(MakeDocument(profile.lines, profile.lineLength));
        std::mt19937 rng(0x0570A11u);
        std::uint64_t checksum = 0;

        const auto start = Clock::now();
        for (std::int32_t i = 0; i < profile.lookupIterations; ++i)
        {
            const auto line = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(profile.lines));
            const auto column = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(profile.lineLength + 1));
            const Position expected{line, column};
            const auto offset = buffer.GetOffsetAt(expected);
            const auto actual = buffer.GetPositionAt(offset);
            if (!(actual == expected))
            {
                throw std::runtime_error("piece-tree position round trip failed");
            }
            checksum += static_cast<std::uint64_t>(offset) + static_cast<std::uint64_t>(actual.lineNumber * 131 + actual.column);
        }
        const auto elapsed = Milliseconds(start, Clock::now());

        Result result{};
        result.name = "piece_tree.position_lookup";
        result.metrics["elapsed_ms"] = elapsed;
        result.metrics["ns_per_roundtrip"] = elapsed * 1'000'000.0 / static_cast<double>(profile.lookupIterations);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["document_utf16"] = static_cast<std::uint64_t>(buffer.Length());
        result.counts["iterations"] = static_cast<std::uint64_t>(profile.lookupIterations);
        result.checksum = checksum;
        return result;
    }

    [[nodiscard]] Result BenchmarkTextModelLocalTyping(const Profile& profile)
    {
        TextModel model(MakeDocument(profile.lines, profile.lineLength));
        const auto middleLine = 1 + profile.lines / 2;
        const Position position{middleLine, 1 + profile.lineLength / 2};
        const auto offset = model.GetOffsetAt(position);
        std::uint64_t checksum = 0;

        const auto start = Clock::now();
        for (std::int32_t i = 0; i < profile.typingEdits; ++i)
        {
            if ((i & 1) == 0)
            {
                model.ApplyOffsetEdits({OffsetEdit{offset, offset, u"X"}}, false);
            }
            else
            {
                model.ApplyOffsetEdits({OffsetEdit{offset, offset + 1, u""}}, false);
            }
            checksum += model.VersionId() + static_cast<std::uint64_t>(model.Length());
        }
        const auto elapsed = Milliseconds(start, Clock::now());

        Result result{};
        result.name = "text_model.local_typing";
        result.metrics["elapsed_ms"] = elapsed;
        result.metrics["us_per_edit"] = elapsed * 1000.0 / static_cast<double>(profile.typingEdits);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["edits"] = static_cast<std::uint64_t>(profile.typingEdits);
        result.counts["final_utf16"] = static_cast<std::uint64_t>(model.Length());
        result.checksum = checksum;
        return result;
    }

    [[nodiscard]] Result BenchmarkTextModelRandomEdits(const Profile& profile)
    {
        TextModel model(MakeDocument(profile.lines, profile.lineLength));
        std::mt19937 rng(0xBADC057u);
        std::uint64_t checksum = 0;

        const auto start = Clock::now();
        for (std::int32_t i = 0; i < profile.randomEdits; ++i)
        {
            const auto lineCount = static_cast<std::int32_t>(model.GetLineCount());
            const auto line = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(lineCount));
            const auto maxColumn = model.GetLineMaxColumn(line);
            const auto column = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(std::max(1, maxColumn)));
            const auto offset = model.GetOffsetAt(Position{line, column});

            if ((i % 3) == 0 && column < maxColumn)
            {
                model.ApplyOffsetEdits({OffsetEdit{offset, offset + 1, u"q"}}, false);
            }
            else if ((i % 3) == 1 && column < maxColumn)
            {
                model.ApplyOffsetEdits({OffsetEdit{offset, offset + 1, u""}}, false);
            }
            else
            {
                model.ApplyOffsetEdits({OffsetEdit{offset, offset, u"z"}}, false);
            }
            checksum += static_cast<std::uint64_t>(model.GetLineMaxColumn(line)) + model.VersionId();
        }
        const auto elapsed = Milliseconds(start, Clock::now());

        Result result{};
        result.name = "text_model.random_same_line_edits";
        result.metrics["elapsed_ms"] = elapsed;
        result.metrics["us_per_edit"] = elapsed * 1000.0 / static_cast<double>(profile.randomEdits);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["edits"] = static_cast<std::uint64_t>(profile.randomEdits);
        result.counts["final_utf16"] = static_cast<std::uint64_t>(model.Length());
        result.checksum = checksum;
        return result;
    }

    [[nodiscard]] Result BenchmarkViewLocalTyping(const Profile& profile)
    {
        auto model = std::make_shared<TextModel>(MakeDocument(profile.lines, profile.lineLength));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        view.SetWrapWidth(static_cast<double>(profile.lineLength / 2));
        measurer->calls = 0;

        const auto middleLine = 1 + profile.lines / 2;
        const auto offset = model->GetOffsetAt(Position{middleLine, 1 + profile.lineLength / 2});
        std::uint64_t maxCalls = 0;
        std::uint64_t maxReplacementModelLines = 0;
        std::uint64_t maxReplacementViewLines = 0;
        std::uint64_t maxSuffixViewLineMetadataRewrites = 0;
        std::uint64_t maxProjectionSlotMoves = 0;
        std::uint64_t checksum = 0;

        const auto start = Clock::now();
        for (std::int32_t i = 0; i < profile.typingEdits; ++i)
        {
            const auto before = measurer->calls;
            if ((i & 1) == 0)
            {
                model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"X"}}, false);
            }
            else
            {
                model->ApplyOffsetEdits({OffsetEdit{offset, offset + 1, u""}}, false);
            }
            const auto delta = measurer->calls - before;
            maxCalls = std::max(maxCalls, delta);
            const auto projectionStats = view.LastProjectionUpdateStats();
            maxReplacementModelLines = std::max(maxReplacementModelLines, projectionStats.replacementModelLines);
            maxReplacementViewLines = std::max(maxReplacementViewLines, projectionStats.replacementViewLines);
            maxSuffixViewLineMetadataRewrites = std::max(
                maxSuffixViewLineMetadataRewrites,
                projectionStats.suffixViewLineMetadataRewrites);
            maxProjectionSlotMoves = std::max(maxProjectionSlotMoves, projectionStats.projectionSlotMoves);
            const auto mapped = view.ConvertModelPositionToViewPosition(Position{middleLine, 1 + profile.lineLength / 2});
            checksum += static_cast<std::uint64_t>(mapped.lineNumber * 131 + mapped.column);
        }
        const auto elapsed = Milliseconds(start, Clock::now());

        Result result{};
        result.name = "view.local_typing_incremental";
        result.metrics["elapsed_ms"] = elapsed;
        result.metrics["us_per_edit"] = elapsed * 1000.0 / static_cast<double>(profile.typingEdits);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["edits"] = static_cast<std::uint64_t>(profile.typingEdits);
        result.counts["measure_calls_total"] = measurer->calls;
        result.counts["measure_calls_per_edit_max"] = maxCalls;
        result.counts["replacement_model_lines_per_edit_max"] = maxReplacementModelLines;
        result.counts["replacement_view_lines_per_edit_max"] = maxReplacementViewLines;
        result.counts["suffix_view_line_metadata_rewrites_per_edit_max"] = maxSuffixViewLineMetadataRewrites;
        result.counts["projection_slot_moves_per_edit_max"] = maxProjectionSlotMoves;
        result.counts["view_lines_final"] = static_cast<std::uint64_t>(view.ViewLineCount());
        result.checksum = checksum;
        return result;
    }

    [[nodiscard]] Result BenchmarkViewStructuralSplice(const Profile& profile)
    {
        auto model = std::make_shared<TextModel>(MakeDocument(profile.lines, profile.lineLength));
        ViewModel view(model);
        auto measurer = std::make_shared<CountingMeasurer>();
        view.SetTextMeasurer(measurer);
        view.SetWrapWidth(static_cast<double>(profile.lineLength / 2));
        measurer->calls = 0;

        const auto middleLine = 1 + profile.lines / 2;
        const auto offset = model->GetOffsetAt(Position{middleLine, 1 + profile.lineLength / 2});
        const auto start = Clock::now();
        model->ApplyOffsetEdits({OffsetEdit{offset, offset, u"\nZZ"}}, false);
        const auto insertCalls = measurer->calls;
        const auto insertProjectionStats = view.LastProjectionUpdateStats();
        const auto afterInsert = Clock::now();
        measurer->calls = 0;
        model->ApplyOffsetEdits({OffsetEdit{offset, offset + 3, u""}}, false);
        const auto deleteCalls = measurer->calls;
        const auto deleteProjectionStats = view.LastProjectionUpdateStats();
        const auto end = Clock::now();

        if (model->GetLineCount() != static_cast<std::size_t>(profile.lines))
        {
            throw std::runtime_error("structural splice did not restore line count");
        }

        Result result{};
        result.name = "view.structural_splice_incremental";
        result.metrics["insert_elapsed_ms"] = Milliseconds(start, afterInsert);
        result.metrics["delete_elapsed_ms"] = Milliseconds(afterInsert, end);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["measure_calls_insert"] = insertCalls;
        result.counts["measure_calls_delete"] = deleteCalls;
        result.counts["suffix_view_line_metadata_rewrites_insert"] = insertProjectionStats.suffixViewLineMetadataRewrites;
        result.counts["suffix_view_line_metadata_rewrites_delete"] = deleteProjectionStats.suffixViewLineMetadataRewrites;
        result.counts["projection_slot_moves_insert"] = insertProjectionStats.projectionSlotMoves;
        result.counts["projection_slot_moves_delete"] = deleteProjectionStats.projectionSlotMoves;
        result.counts["replacement_model_lines_insert"] = insertProjectionStats.replacementModelLines;
        result.counts["replacement_model_lines_delete"] = deleteProjectionStats.replacementModelLines;
        result.counts["view_lines_final"] = static_cast<std::uint64_t>(view.ViewLineCount());
        result.checksum = static_cast<std::uint64_t>(view.ViewLineCount()) * 131u + static_cast<std::uint64_t>(model->Length());
        return result;
    }

    [[nodiscard]] Result BenchmarkViewMappingRoundTrip(const Profile& profile)
    {
        auto model = std::make_shared<TextModel>(MakeDocument(profile.lines, profile.lineLength));
        ViewModel view(model);
        view.SetWrapColumn(std::max(1, profile.lineLength / 3));
        std::mt19937 rng(0xC0DE057u);
        std::uint64_t checksum = 0;

        const auto start = Clock::now();
        for (std::int32_t i = 0; i < profile.mappingIterations; ++i)
        {
            const auto line = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(profile.lines));
            const auto column = 1 + static_cast<std::int32_t>(rng() % static_cast<std::uint32_t>(profile.lineLength + 1));
            const Position expected{line, column};
            const auto viewPosition = view.ConvertModelPositionToViewPosition(expected);
            const auto actual = view.ConvertViewPositionToModelPosition(viewPosition);
            if (!(actual == expected))
            {
                throw std::runtime_error("view/model mapping round trip failed");
            }
            checksum += static_cast<std::uint64_t>(viewPosition.lineNumber * 131 + viewPosition.column);
        }
        const auto elapsed = Milliseconds(start, Clock::now());

        Result result{};
        result.name = "view.mapping_roundtrip";
        result.metrics["elapsed_ms"] = elapsed;
        result.metrics["ns_per_roundtrip"] = elapsed * 1'000'000.0 / static_cast<double>(profile.mappingIterations);
        result.counts["document_lines"] = static_cast<std::uint64_t>(profile.lines);
        result.counts["view_lines"] = static_cast<std::uint64_t>(view.ViewLineCount());
        result.counts["iterations"] = static_cast<std::uint64_t>(profile.mappingIterations);
        result.checksum = checksum;
        return result;
    }

    [[nodiscard]] std::string EscapeJson(std::string_view value)
    {
        std::ostringstream out;
        for (const char ch : value)
        {
            switch (ch)
            {
            case '\\': out << "\\\\"; break;
            case '"': out << "\\\""; break;
            case '\n': out << "\\n"; break;
            case '\r': out << "\\r"; break;
            case '\t': out << "\\t"; break;
            default: out << ch; break;
            }
        }
        return out.str();
    }

    [[nodiscard]] std::string CompilerName()
    {
#if defined(__clang__)
        return "clang";
#elif defined(__GNUC__)
        return "gcc";
#elif defined(_MSC_VER)
        return "msvc";
#else
        return "unknown";
#endif
    }

    [[nodiscard]] std::string Serialize(const Profile& profile, const std::vector<Result>& results)
    {
        std::ostringstream out;
        out << std::fixed << std::setprecision(6);
        out << "{\n";
        out << "  \"schemaVersion\": 1,\n";
        out << "  \"suite\": \"core-native\",\n";
        out << "  \"profile\": \"" << EscapeJson(profile.name) << "\",\n";
        out << "  \"monacoEditorVersion\": \"0.57.0\",\n";
        out << "  \"vscodeRef\": \"6a598d4a13031703d483d103c1d934a36ad27971\",\n";
        out << "  \"compiler\": \"" << CompilerName() << "\",\n";
        out << "  \"results\": [\n";
        for (std::size_t i = 0; i < results.size(); ++i)
        {
            const auto& result = results[i];
            out << "    {\n";
            out << "      \"name\": \"" << EscapeJson(result.name) << "\",\n";
            out << "      \"metrics\": {";
            std::size_t metricIndex = 0;
            for (const auto& [key, value] : result.metrics)
            {
                out << (metricIndex++ == 0 ? "\n" : ",\n");
                out << "        \"" << EscapeJson(key) << "\": " << value;
            }
            if (!result.metrics.empty())
            {
                out << "\n      ";
            }
            out << "},\n";
            out << "      \"counts\": {";
            std::size_t countIndex = 0;
            for (const auto& [key, value] : result.counts)
            {
                out << (countIndex++ == 0 ? "\n" : ",\n");
                out << "        \"" << EscapeJson(key) << "\": " << value;
            }
            if (!result.counts.empty())
            {
                out << "\n      ";
            }
            out << "},\n";
            out << "      \"checksum\": " << result.checksum << "\n";
            out << "    }" << (i + 1 == results.size() ? "\n" : ",\n");
        }
        out << "  ]\n";
        out << "}\n";
        return out.str();
    }

    [[nodiscard]] Profile ParseProfile(std::string_view name)
    {
        if (name == "quick")
        {
            return Profile{"quick", 20'000, 48, 40'000, 300, 500, 40'000};
        }
        if (name == "full")
        {
            return Profile{"full", 100'000, 64, 250'000, 1'500, 3'000, 250'000};
        }
        throw std::invalid_argument("profile must be 'quick' or 'full'");
    }
}

int main(int argc, char** argv)
{
    try
    {
        std::string profileName = "quick";
        std::string outputPath;
        for (int i = 1; i < argc; ++i)
        {
            const std::string_view arg(argv[i]);
            if (arg == "--profile" && i + 1 < argc)
            {
                profileName = argv[++i];
            }
            else if (arg == "--output" && i + 1 < argc)
            {
                outputPath = argv[++i];
            }
            else if (arg == "--help")
            {
                std::cout << "Usage: MonacoNative.Core.Benchmarks [--profile quick|full] [--output result.json]\n";
                return 0;
            }
            else
            {
                throw std::invalid_argument("unknown or incomplete argument: " + std::string(arg));
            }
        }

        const auto profile = ParseProfile(profileName);
        std::vector<Result> results;
        results.push_back(BenchmarkPieceTreePositionLookup(profile));
        results.push_back(BenchmarkTextModelLocalTyping(profile));
        results.push_back(BenchmarkTextModelRandomEdits(profile));
        results.push_back(BenchmarkViewLocalTyping(profile));
        results.push_back(BenchmarkViewStructuralSplice(profile));
        results.push_back(BenchmarkViewMappingRoundTrip(profile));

        const auto json = Serialize(profile, results);
        if (!outputPath.empty())
        {
            std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                throw std::runtime_error("failed to open benchmark output: " + outputPath);
            }
            output << json;
        }
        else
        {
            std::cout << json;
        }
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << "benchmark failure: " << ex.what() << '\n';
        return 1;
    }
}
