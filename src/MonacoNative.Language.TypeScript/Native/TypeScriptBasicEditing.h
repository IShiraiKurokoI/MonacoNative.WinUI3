// Small adaptations from Microsoft TypeScript 5.8.3 (Apache-2.0).
// signatureHelp.ts:getArgumentIndexOrCount/rest-parameter highlighting;
// outliningElementsCollector.ts:parseRegionDelimiter. See THIRD_PARTY_NOTICES.md.
// Argument-node construction, binding and editor line projection are host subset
// code, not a translation of the upstream parser, checker or complete outliner.
#pragma once
#include "TypeScriptScanner.h"
#include <optional>

namespace mn::typescript
{
enum class ArgumentKind { Expression, Comma, Spread };
struct ArgumentChild
{
    ArgumentKind kind = ArgumentKind::Expression;
    size_t start = 0, end = 0;
    size_t spreadCount = 0; // unknown/non-tuple spread has zero known fixed entries
};
inline size_t argument_index_or_count(const std::vector<ArgumentChild>& args, std::optional<size_t> node = {})
{
    size_t argumentIndex = 0;
    bool skipComma = false;
    for (size_t i = 0; i < args.size(); ++i)
    {
        const auto& child = args[i];
        if (node && i == *node)
        {
            if (!skipComma && child.kind == ArgumentKind::Comma) ++argumentIndex;
            return argumentIndex;
        }
        if (child.kind == ArgumentKind::Spread)
        {
            argumentIndex += child.spreadCount;
            skipComma = true;
            continue;
        }
        if (child.kind != ArgumentKind::Comma)
        {
            ++argumentIndex;
            skipComma = true;
            continue;
        }
        if (skipComma) { skipComma = false; continue; }
        ++argumentIndex;
    }
    if (node) return argumentIndex;
    return !args.empty() && args.back().kind == ArgumentKind::Comma ? argumentIndex + 1 : argumentIndex;
}
inline size_t highlighted_parameter(size_t index, size_t parameterCount, std::optional<size_t> firstRest)
{
    if (!firstRest || parameterCount == 0) return index;
    // Same sentinel as upstream: no highlighted parameter for a middle rest.
    if (*firstRest < parameterCount - 1) return parameterCount;
    return std::min(index, parameterCount - 1);
}
struct RegionDelimiter { bool isStart; std::u16string name; };
inline std::optional<RegionDelimiter> parse_region_delimiter(std::u16string_view line)
{
    const auto trimStart = [&]() { while (!line.empty() && (space(line.front()) || line_break(line.front()))) line.remove_prefix(1); };
    trimStart();
    if (!line.starts_with(u"//")) return {};
    line.remove_prefix(2); trimStart();
    while (!line.empty() && (space(line.back()) || line_break(line.back()))) line.remove_suffix(1);
    const bool start = line.starts_with(u"#region");
    if (!start && !line.starts_with(u"#endregion")) return {};
    line.remove_prefix(start ? 7 : 10); trimStart();
    while (!line.empty() && (space(line.back()) || line_break(line.back()))) line.remove_suffix(1);
    // The upstream regular expression deliberately also accepts '#regional'.
    return RegionDelimiter{start, std::u16string(line)};
}
}
