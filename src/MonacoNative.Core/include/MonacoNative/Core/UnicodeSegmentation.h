#pragma once

#include <cstddef>
#include <string_view>

namespace MonacoNative::Core::Unicode
{
    // UTF-16 boundary helpers for the MonacoNative Unicode 18 editor profile. The
    // profile follows UAX #29 extended-grapheme rule ordering (including revised GB9c)
    // for cursor/edit safety, while keeping a compact embedded property table.
    // Offsets are UTF-16 code-unit offsets and returned values are always valid boundaries.
    std::size_t PreviousGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept;
    std::size_t NextGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept;
    bool IsGraphemeBoundary(std::u16string_view text, std::size_t offset) noexcept;

    // Editor-oriented word navigation informed by UAX #29 word-boundary classes. This is
    // intentionally a Monaco-style tailoring rather than a claim of default UAX #29 word
    // breaking: punctuation/CJK stepping and trailing-whitespace skipping match editor use.
    std::size_t PreviousWordBoundary(std::u16string_view text, std::size_t offset) noexcept;
    std::size_t NextWordBoundary(std::u16string_view text, std::size_t offset) noexcept;
}
