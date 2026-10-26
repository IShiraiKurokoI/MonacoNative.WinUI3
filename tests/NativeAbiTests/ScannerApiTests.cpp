// Apache-2.0; Microsoft TypeScript source/oracle attribution: THIRD_PARTY_NOTICES.md.
// Frozen differential cases from official TypeScript 5.8.3 scanner.ts APIs.
// This is NOT a transcription of the complete TypeScript compiler test suite.
// Expected states were captured from lib/typescript.js, SHA256:
// dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0
// Test execution is entirely C++; no Node.js, npm or Python is invoked.
#include "../../src/MonacoNative.Language.TypeScript/Native/TypeScriptScanner.h"
#include <iostream>
#include <stdexcept>
#include <string_view>
namespace {
using mn::typescript::Scanner;
using mn::typescript::Token;
using mn::language::Text;
enum class Op { asterisk, attr, greater, hash, jsdoc, jsdocCode, jsdocText, jsx, jsxIdentifier, jsxSingle, less, question, rescanAttr, rescanJsx, scan, slash, tagged, templateToken };
struct ErrorSpan { size_t start, end; };
struct State {
    Op op; int returned, stored; size_t start, end, full; unsigned flags;
    std::string_view value; std::vector<ErrorSpan> errors;
};
struct Fixture { std::string_view name, source; bool skip, jsx; std::vector<State> states; };
const std::vector<Fixture> fixtures{
    {{"greater >>", 10}, {">>", 2}, true, false, {
        {Op::scan, 32, 32, 0, 1, 0, 0, {"", 0}, {}},
        {Op::greater, 49, 49, 0, 2, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"", 0}, {}},
    }},
    {{"greater >>>", 11}, {">>>", 3}, true, false, {
        {Op::scan, 32, 32, 0, 1, 0, 0, {"", 0}, {}},
        {Op::greater, 50, 50, 0, 3, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 3, 3, 3, 0, {"", 0}, {}},
    }},
    {{"greater >=", 10}, {">=", 2}, true, false, {
        {Op::scan, 32, 32, 0, 1, 0, 0, {"", 0}, {}},
        {Op::greater, 34, 34, 0, 2, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"", 0}, {}},
    }},
    {{"greater >>=", 11}, {">>=", 3}, true, false, {
        {Op::scan, 32, 32, 0, 1, 0, 0, {"", 0}, {}},
        {Op::greater, 72, 72, 0, 3, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 3, 3, 3, 0, {"", 0}, {}},
    }},
    {{"greater >>>=", 12}, {">>>=", 4}, true, false, {
        {Op::scan, 32, 32, 0, 1, 0, 0, {"", 0}, {}},
        {Op::greater, 73, 73, 0, 4, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"", 0}, {}},
    }},
    {{"greater  > >", 12}, {" > >", 4}, true, false, {
        {Op::scan, 32, 32, 1, 2, 0, 0, {"", 0}, {}},
        {Op::greater, 32, 32, 1, 2, 0, 0, {"", 0}, {}},
        {Op::scan, 32, 32, 3, 4, 2, 0, {"", 0}, {}},
        {Op::greater, 32, 32, 3, 4, 2, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"", 0}, {}},
    }},
    {{"less <<", 7}, {"<<", 2}, true, false, {
        {Op::scan, 48, 48, 0, 2, 0, 0, {"", 0}, {}},
        {Op::less, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 30, 30, 1, 2, 1, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"", 0}, {}},
    }},
    {{"less <<=", 8}, {"<<=", 3}, true, false, {
        {Op::scan, 71, 71, 0, 3, 0, 0, {"", 0}, {}},
        {Op::less, 71, 71, 0, 3, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 3, 3, 3, 0, {"", 0}, {}},
    }},
    {{"less <", 6}, {"<", 1}, true, false, {
        {Op::scan, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::less, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 1, 1, 1, 0, {"", 0}, {}},
    }},
    {{"less  <tag", 10}, {" <tag", 5}, true, false, {
        {Op::scan, 30, 30, 1, 2, 0, 0, {"", 0}, {}},
        {Op::less, 30, 30, 1, 2, 0, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 2, 5, 2, 0, {"tag", 3}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"tag", 3}, {}},
    }},
    {{"hash #field", 11}, {"#field", 6}, true, false, {
        {Op::scan, 81, 81, 0, 6, 0, 0, {"#field", 6}, {}},
        {Op::hash, 63, 63, 0, 1, 0, 0, {"#field", 6}, {}},
        {Op::scan, 80, 80, 1, 6, 1, 0, {"field", 5}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"field", 5}, {}},
    }},
    {{"hash #\\u0061", 12}, {"#\\u0061", 7}, true, false, {
        {Op::scan, 81, 81, 0, 7, 0, 1024, {"#a", 2}, {}},
        {Op::hash, 63, 63, 0, 1, 0, 1024, {"#a", 2}, {}},
        {Op::scan, 80, 80, 1, 7, 1, 1024, {"a", 1}, {}},
        {Op::scan, 1, 1, 7, 7, 7, 0, {"a", 1}, {}},
    }},
    {{"hash ##", 7}, {"##", 2}, true, false, {
        {Op::scan, 81, 81, 0, 1, 0, 0, {"#", 1}, {{0, 1}}},
        {Op::hash, 63, 63, 0, 1, 0, 0, {"#", 1}, {}},
        {Op::scan, 81, 81, 1, 2, 1, 0, {"#", 1}, {{1, 2}}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"#", 1}, {}},
    }},
    {{"hash  #name", 11}, {" #name", 6}, true, false, {
        {Op::scan, 81, 81, 1, 6, 0, 0, {"#name", 5}, {}},
        {Op::hash, 63, 63, 1, 2, 0, 0, {"#name", 5}, {}},
        {Op::scan, 80, 80, 2, 6, 2, 0, {"name", 4}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"name", 4}, {}},
    }},
    {{"question split", 14}, {"?? x", 4}, true, false, {
        {Op::scan, 61, 61, 0, 2, 0, 0, {"", 0}, {}},
        {Op::question, 58, 58, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 58, 58, 1, 2, 1, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 3, 4, 2, 0, {"x", 1}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"x", 1}, {}},
    }},
    {{"asterisk split", 14}, {"*= x", 4}, true, false, {
        {Op::scan, 67, 67, 0, 2, 0, 0, {"", 0}, {}},
        {Op::asterisk, 64, 64, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 64, 64, 1, 2, 1, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 3, 4, 2, 0, {"x", 1}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"x", 1}, {}},
    }},
    {{"regex /a+/gi", 12}, {"/a+/gi", 6}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 6, 0, 0, {"/a+/gi", 6}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"/a+/gi", 6}, {}},
    }},
    {{"regex /[a/b]+/u", 15}, {"/[a/b]+/u", 9}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 9, 0, 0, {"/[a/b]+/u", 9}, {}},
        {Op::scan, 1, 1, 9, 9, 9, 0, {"/[a/b]+/u", 9}, {}},
    }},
    {{"regex /foo\\/bar/d", 17}, {"/foo\\/bar/d", 11}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 11, 0, 0, {"/foo\\/bar/d", 11}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"/foo\\/bar/d", 11}, {}},
    }},
    {{"regex /unterminated);", 21}, {"/unterminated);", 15}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 13, 0, 4, {"/unterminated", 13}, {{0, 13}}},
        {Op::scan, 22, 22, 13, 14, 13, 0, {"/unterminated", 13}, {}},
        {Op::scan, 27, 27, 14, 15, 14, 0, {"/unterminated", 13}, {}},
        {Op::scan, 1, 1, 15, 15, 15, 0, {"/unterminated", 13}, {}},
    }},
    {{"regex /[abc]/", 13}, {"/[abc]/", 7}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 7, 0, 0, {"/[abc]/", 7}, {}},
        {Op::scan, 1, 1, 7, 7, 7, 0, {"/[abc]/", 7}, {}},
    }},
    {{"regex /foo{2,3}]; tail", 22}, {"/foo{2,3}]; tail", 16}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 9, 0, 4, {"/foo{2,3}", 9}, {{0, 9}}},
        {Op::scan, 24, 24, 9, 10, 9, 0, {"/foo{2,3}", 9}, {}},
        {Op::scan, 27, 27, 10, 11, 10, 0, {"/foo{2,3}", 9}, {}},
        {Op::scan, 80, 80, 12, 16, 11, 0, {"tail", 4}, {}},
    }},
    {{"regex /(abc) ;\012next", 19}, {"/(abc) ;\012next", 13}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 6, 0, 4, {"/(abc)", 6}, {{0, 6}}},
        {Op::scan, 27, 27, 7, 8, 6, 0, {"/(abc)", 6}, {}},
        {Op::scan, 80, 80, 9, 13, 8, 1, {"next", 4}, {}},
        {Op::scan, 1, 1, 13, 13, 13, 0, {"next", 4}, {}},
    }},
    {{"regex /abc\\", 11}, {"/abc\\", 5}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 5, 0, 4, {"/abc\\", 5}, {{0, 5}}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"/abc\\", 5}, {}},
    }},
    {{"regex /=x/i", 11}, {"/=x/i", 5}, true, false, {
        {Op::scan, 69, 69, 0, 2, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 5, 0, 0, {"/=x/i", 5}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"/=x/i", 5}, {}},
    }},
    {{"regex /a/vx", 11}, {"/a/vx", 5}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 5, 0, 0, {"/a/vx", 5}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"/a/vx", 5}, {}},
    }},
    {{"regex /[(] ;;", 13}, {"/[(] ;;", 7}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 4, 0, 4, {"/[(]", 4}, {{0, 4}}},
        {Op::scan, 27, 27, 5, 6, 4, 0, {"/[(]", 4}, {}},
        {Op::scan, 27, 27, 6, 7, 6, 0, {"/[(]", 4}, {}},
        {Op::scan, 1, 1, 7, 7, 7, 0, {"/[(]", 4}, {}},
    }},
    {{"regex /;", 8}, {"/;", 2}, true, false, {
        {Op::scan, 44, 44, 0, 1, 0, 0, {"", 0}, {}},
        {Op::slash, 14, 14, 0, 1, 0, 4, {"/", 1}, {{0, 1}}},
        {Op::scan, 27, 27, 1, 2, 1, 0, {"/", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"/", 1}, {}},
    }},
    {{"template }tail`", 15}, {"}tail`", 6}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::templateToken, 18, 18, 0, 6, 0, 0, {"tail", 4}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"tail", 4}, {}},
    }},
    {{"tagged }tail`", 13}, {"}tail`", 6}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::tagged, 18, 18, 0, 6, 0, 0, {"tail", 4}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"tail", 4}, {}},
    }},
    {{"template }mid${x}", 17}, {"}mid${x}", 8}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::templateToken, 17, 17, 0, 6, 0, 0, {"mid", 3}, {}},
        {Op::scan, 80, 80, 6, 7, 6, 0, {"x", 1}, {}},
        {Op::scan, 20, 20, 7, 8, 7, 0, {"x", 1}, {}},
    }},
    {{"tagged }mid${x}", 15}, {"}mid${x}", 8}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::tagged, 17, 17, 0, 6, 0, 0, {"mid", 3}, {}},
        {Op::scan, 80, 80, 6, 7, 6, 0, {"x", 1}, {}},
        {Op::scan, 20, 20, 7, 8, 7, 0, {"x", 1}, {}},
    }},
    {{"template }\\xZ`", 14}, {"}\\xZ`", 5}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::templateToken, 18, 18, 0, 5, 0, 2048, {"\\xZ", 3}, {{3, 3}}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"\\xZ", 3}, {}},
    }},
    {{"tagged }\\xZ`", 12}, {"}\\xZ`", 5}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::tagged, 18, 18, 0, 5, 0, 2048, {"\\xZ", 3}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"\\xZ", 3}, {}},
    }},
    {{"template }\\u{1F600}`", 20}, {"}\\u{1F600}`", 11}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::templateToken, 18, 18, 0, 11, 0, 8, {"\360\237\230\200", 4}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"\360\237\230\200", 4}, {}},
    }},
    {{"tagged }\\u{1F600}`", 18}, {"}\\u{1F600}`", 11}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::tagged, 18, 18, 0, 11, 0, 8, {"\360\237\230\200", 4}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"\360\237\230\200", 4}, {}},
    }},
    {{"template }a\015\012b`", 15}, {"}a\015\012b`", 6}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::templateToken, 18, 18, 0, 6, 0, 0, {"a\012b", 3}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"a\012b", 3}, {}},
    }},
    {{"tagged }a\015\012b`", 13}, {"}a\015\012b`", 6}, false, false, {
        {Op::scan, 20, 20, 0, 1, 0, 0, {"", 0}, {}},
        {Op::tagged, 18, 18, 0, 6, 0, 0, {"a\012b", 3}, {}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"a\012b", 3}, {}},
    }},
    {{"jsx text hello <A>", 18}, {"hello <A>", 9}, false, false, {
        {Op::jsx, 12, 0, 0, 6, 0, 0, {"hello ", 6}, {}},
        {Op::jsx, 30, 30, 6, 7, 6, 0, {"hello ", 6}, {}},
        {Op::jsx, 12, 30, 7, 9, 7, 0, {"A>", 2}, {{8, 9}}},
        {Op::jsx, 1, 1, 9, 9, 9, 0, {"A>", 2}, {}},
    }},
    {{"jsx single hello <A>", 20}, {"hello <A>", 9}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 6, 0, 0, {"hello ", 6}, {}},
        {Op::jsxSingle, 30, 30, 6, 7, 6, 0, {"hello ", 6}, {}},
        {Op::jsxSingle, 12, 30, 7, 9, 7, 0, {"A>", 2}, {{8, 9}}},
        {Op::jsxSingle, 1, 1, 9, 9, 9, 0, {"A>", 2}, {}},
    }},
    {{"jsx text \012  \012<A/>", 17}, {"\012  \012<A/>", 8}, false, false, {
        {Op::jsx, 13, 0, 0, 4, 0, 0, {"\012  \012", 4}, {}},
        {Op::jsx, 30, 30, 4, 5, 4, 0, {"\012  \012", 4}, {}},
        {Op::jsx, 12, 30, 5, 8, 5, 0, {"A/>", 3}, {{7, 8}}},
        {Op::jsx, 1, 1, 8, 8, 8, 0, {"A/>", 3}, {}},
    }},
    {{"jsx single \012  \012<A/>", 19}, {"\012  \012<A/>", 8}, false, false, {
        {Op::jsxSingle, 13, 0, 0, 4, 0, 0, {"\012  \012", 4}, {}},
        {Op::jsxSingle, 30, 30, 4, 5, 4, 0, {"\012  \012", 4}, {}},
        {Op::jsxSingle, 12, 30, 5, 8, 5, 0, {"A/>", 3}, {{7, 8}}},
        {Op::jsxSingle, 1, 1, 8, 8, 8, 0, {"A/>", 3}, {}},
    }},
    {{"jsx text  hi\012next {x}", 21}, {" hi\012next {x}", 12}, false, false, {
        {Op::jsx, 12, 0, 0, 9, 0, 0, {" hi\012next ", 9}, {}},
        {Op::jsx, 19, 19, 9, 10, 9, 0, {" hi\012next ", 9}, {}},
        {Op::jsx, 12, 19, 10, 12, 10, 0, {"x}", 2}, {{11, 12}}},
        {Op::jsx, 1, 1, 12, 12, 12, 0, {"x}", 2}, {}},
    }},
    {{"jsx single  hi\012next {x}", 23}, {" hi\012next {x}", 12}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 3, 0, 0, {" hi", 3}, {}},
        {Op::jsxSingle, 12, 0, 3, 9, 3, 0, {"\012next ", 6}, {}},
        {Op::jsxSingle, 19, 19, 9, 10, 9, 0, {"\012next ", 6}, {}},
        {Op::jsxSingle, 12, 19, 10, 12, 10, 0, {"x}", 2}, {{11, 12}}},
        {Op::jsxSingle, 1, 1, 12, 12, 12, 0, {"x}", 2}, {}},
    }},
    {{"jsx text a\012b\012c", 14}, {"a\012b\012c", 5}, false, false, {
        {Op::jsx, 12, 0, 0, 5, 0, 0, {"a\012b\012c", 5}, {}},
        {Op::jsx, 1, 1, 5, 5, 5, 0, {"a\012b\012c", 5}, {}},
    }},
    {{"jsx single a\012b\012c", 16}, {"a\012b\012c", 5}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 3, 0, 0, {"a\012b", 3}, {}},
        {Op::jsxSingle, 12, 0, 3, 5, 3, 0, {"\012c", 2}, {}},
        {Op::jsxSingle, 1, 1, 5, 5, 5, 0, {"\012c", 2}, {}},
    }},
    {{"jsx text <>{x}</>", 17}, {"<>{x}</>", 8}, false, false, {
        {Op::jsx, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsx, 12, 30, 1, 2, 1, 0, {">", 1}, {{1, 2}}},
        {Op::jsx, 19, 19, 2, 3, 2, 0, {">", 1}, {}},
        {Op::jsx, 12, 19, 3, 5, 3, 0, {"x}", 2}, {{4, 5}}},
        {Op::jsx, 31, 31, 5, 7, 5, 0, {"x}", 2}, {}},
        {Op::jsx, 12, 31, 7, 8, 7, 0, {">", 1}, {{7, 8}}},
        {Op::jsx, 1, 1, 8, 8, 8, 0, {">", 1}, {}},
    }},
    {{"jsx single <>{x}</>", 19}, {"<>{x}</>", 8}, false, false, {
        {Op::jsxSingle, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsxSingle, 12, 30, 1, 2, 1, 0, {">", 1}, {{1, 2}}},
        {Op::jsxSingle, 19, 19, 2, 3, 2, 0, {">", 1}, {}},
        {Op::jsxSingle, 12, 19, 3, 5, 3, 0, {"x}", 2}, {{4, 5}}},
        {Op::jsxSingle, 31, 31, 5, 7, 5, 0, {"x}", 2}, {}},
        {Op::jsxSingle, 12, 31, 7, 8, 7, 0, {">", 1}, {{7, 8}}},
        {Op::jsxSingle, 1, 1, 8, 8, 8, 0, {">", 1}, {}},
    }},
    {{"jsx text bad > } text", 21}, {"bad > } text", 12}, false, false, {
        {Op::jsx, 12, 0, 0, 12, 0, 0, {"bad > } text", 12}, {{4, 5}, {6, 7}}},
        {Op::jsx, 1, 1, 12, 12, 12, 0, {"bad > } text", 12}, {}},
    }},
    {{"jsx single bad > } text", 23}, {"bad > } text", 12}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 12, 0, 0, {"bad > } text", 12}, {{4, 5}, {6, 7}}},
        {Op::jsxSingle, 1, 1, 12, 12, 12, 0, {"bad > } text", 12}, {}},
    }},
    {{"jsx text \302\205 \012<A>", 16}, {"\302\205 \012<A>", 7}, false, false, {
        {Op::jsx, 13, 0, 0, 3, 0, 0, {"\302\205 \012", 4}, {}},
        {Op::jsx, 30, 30, 3, 4, 3, 0, {"\302\205 \012", 4}, {}},
        {Op::jsx, 12, 30, 4, 6, 4, 0, {"A>", 2}, {{5, 6}}},
        {Op::jsx, 1, 1, 6, 6, 6, 0, {"A>", 2}, {}},
    }},
    {{"jsx single \302\205 \012<A>", 18}, {"\302\205 \012<A>", 7}, false, false, {
        {Op::jsxSingle, 13, 0, 0, 3, 0, 0, {"\302\205 \012", 4}, {}},
        {Op::jsxSingle, 30, 30, 3, 4, 3, 0, {"\302\205 \012", 4}, {}},
        {Op::jsxSingle, 12, 30, 4, 6, 4, 0, {"A>", 2}, {{5, 6}}},
        {Op::jsxSingle, 1, 1, 6, 6, 6, 0, {"A>", 2}, {}},
    }},
    {{"jsx text \360\237\230\200text<{", 19}, {"\360\237\230\200text<{", 10}, false, false, {
        {Op::jsx, 12, 0, 0, 6, 0, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsx, 30, 30, 6, 7, 6, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsx, 19, 19, 7, 8, 7, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsx, 1, 1, 8, 8, 8, 0, {"\360\237\230\200text", 8}, {}},
    }},
    {{"jsx single \360\237\230\200text<{", 21}, {"\360\237\230\200text<{", 10}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 6, 0, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsxSingle, 30, 30, 6, 7, 6, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsxSingle, 19, 19, 7, 8, 7, 0, {"\360\237\230\200text", 8}, {}},
        {Op::jsxSingle, 1, 1, 8, 8, 8, 0, {"\360\237\230\200text", 8}, {}},
    }},
    {{"jsx text ", 9}, {"", 0}, false, false, {
        {Op::jsx, 1, 1, 0, 0, 0, 0, {"", 0}, {}},
    }},
    {{"jsx single ", 11}, {"", 0}, false, false, {
        {Op::jsxSingle, 1, 1, 0, 0, 0, 0, {"", 0}, {}},
    }},
    {{"jsx text  ", 10}, {" ", 1}, false, false, {
        {Op::jsx, 12, 0, 0, 1, 0, 0, {" ", 1}, {}},
        {Op::jsx, 1, 1, 1, 1, 1, 0, {" ", 1}, {}},
    }},
    {{"jsx single  ", 12}, {" ", 1}, false, false, {
        {Op::jsxSingle, 12, 0, 0, 1, 0, 0, {" ", 1}, {}},
        {Op::jsxSingle, 1, 1, 1, 1, 1, 0, {" ", 1}, {}},
    }},
    {{"jsx text \012", 10}, {"\012", 1}, false, false, {
        {Op::jsx, 13, 0, 0, 1, 0, 0, {"\012", 1}, {}},
        {Op::jsx, 1, 1, 1, 1, 1, 0, {"\012", 1}, {}},
    }},
    {{"jsx single \012", 12}, {"\012", 1}, false, false, {
        {Op::jsxSingle, 13, 0, 0, 1, 0, 0, {"\012", 1}, {}},
        {Op::jsxSingle, 1, 1, 1, 1, 1, 0, {"\012", 1}, {}},
    }},
    {{"jsx rescan   hello\012world <x>", 28}, {"  hello\012world <x>", 17}, true, true, {
        {Op::scan, 80, 80, 2, 7, 0, 0, {"hello", 5}, {}},
        {Op::rescanJsx, 12, 12, 0, 14, 0, 0, {"  hello\012world ", 14}, {}},
        {Op::scan, 30, 30, 14, 15, 14, 0, {"  hello\012world ", 14}, {}},
        {Op::jsx, 12, 30, 15, 17, 15, 0, {"x>", 2}, {{16, 17}}},
        {Op::scan, 1, 1, 17, 17, 17, 0, {"x>", 2}, {}},
    }},
    {{"jsx rescan name {x}", 19}, {"name {x}", 8}, true, true, {
        {Op::scan, 80, 80, 0, 4, 0, 0, {"name", 4}, {}},
        {Op::rescanJsx, 12, 12, 0, 5, 0, 0, {"name ", 5}, {}},
        {Op::scan, 19, 19, 5, 6, 5, 0, {"name ", 5}, {}},
        {Op::jsx, 12, 19, 6, 8, 6, 0, {"x}", 2}, {{7, 8}}},
        {Op::scan, 1, 1, 8, 8, 8, 0, {"x}", 2}, {}},
    }},
    {{"jsx rescan <tag/>", 17}, {"<tag/>", 6}, true, true, {
        {Op::scan, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::rescanJsx, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 1, 4, 1, 0, {"tag", 3}, {}},
        {Op::jsx, 12, 80, 4, 6, 4, 0, {"/>", 2}, {{5, 6}}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {"/>", 2}, {}},
    }},
    {{"jsx rescan \012  \012<>", 17}, {"\012  \012<>", 6}, true, true, {
        {Op::scan, 30, 30, 4, 5, 0, 1, {"", 0}, {}},
        {Op::rescanJsx, 13, 13, 0, 4, 0, 1, {"\012  \012", 4}, {}},
        {Op::scan, 30, 30, 4, 5, 4, 0, {"\012  \012", 4}, {}},
        {Op::jsx, 12, 30, 5, 6, 5, 0, {">", 1}, {{5, 6}}},
        {Op::scan, 1, 1, 6, 6, 6, 0, {">", 1}, {}},
    }},
    {{"jsx name data-value", 19}, {"data-value", 10}, true, true, {
        {Op::scan, 80, 80, 0, 4, 0, 0, {"data", 4}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 10, 0, 0, {"data-value", 10}, {}},
        {Op::scan, 1, 1, 10, 10, 10, 0, {"data-value", 10}, {}},
    }},
    {{"jsx name class-name", 19}, {"class-name", 10}, true, true, {
        {Op::scan, 86, 86, 0, 5, 0, 0, {"class", 5}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 10, 0, 0, {"class-name", 10}, {}},
        {Op::scan, 1, 1, 10, 10, 10, 0, {"class-name", 10}, {}},
    }},
    {{"jsx name onClick", 16}, {"onClick", 7}, true, true, {
        {Op::scan, 80, 80, 0, 7, 0, 0, {"onClick", 7}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 7, 0, 0, {"onClick", 7}, {}},
        {Op::scan, 1, 1, 7, 7, 7, 0, {"onClick", 7}, {}},
    }},
    {{"jsx name aria-\\u0061", 20}, {"aria-\\u0061", 11}, true, true, {
        {Op::scan, 80, 80, 0, 4, 0, 0, {"aria", 4}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 11, 0, 1024, {"aria-a", 6}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"aria-a", 6}, {}},
    }},
    {{"jsx name custom-\345\205\203\347\264\240", 22}, {"custom-\345\205\203\347\264\240", 13}, true, true, {
        {Op::scan, 80, 80, 0, 6, 0, 0, {"custom", 6}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 9, 0, 0, {"custom-\345\205\203\347\264\240", 13}, {}},
        {Op::scan, 1, 1, 9, 9, 9, 0, {"custom-\345\205\203\347\264\240", 13}, {}},
    }},
    {{"jsx name x:Name", 15}, {"x:Name", 6}, true, true, {
        {Op::scan, 80, 80, 0, 1, 0, 0, {"x", 1}, {}},
        {Op::jsxIdentifier, 80, 80, 0, 1, 0, 0, {"x", 1}, {}},
        {Op::scan, 59, 59, 1, 2, 1, 0, {"x", 1}, {}},
        {Op::scan, 80, 80, 2, 6, 2, 0, {"Name", 4}, {}},
    }},
    {{"jsx attribute \"a\\n\"", 19}, {"\"a\\n\"", 5}, true, true, {
        {Op::scan, 11, 11, 0, 5, 0, 0, {"a\012", 2}, {}},
        {Op::rescanAttr, 11, 11, 0, 5, 0, 0, {"a\\n", 3}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"a\\n", 3}, {}},
    }},
    {{"jsx attribute \"line\012line\"", 25}, {"\"line\012line\"", 11}, true, true, {
        {Op::scan, 11, 11, 0, 5, 0, 4, {"line", 4}, {{5, 5}}},
        {Op::rescanAttr, 11, 11, 0, 11, 0, 4, {"line\012line", 9}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"line\012line", 9}, {}},
    }},
    {{"jsx attribute 'x&copy;y'", 24}, {"'x&copy;y'", 10}, true, true, {
        {Op::scan, 11, 11, 0, 10, 0, 0, {"x&copy;y", 8}, {}},
        {Op::rescanAttr, 11, 11, 0, 10, 0, 0, {"x&copy;y", 8}, {}},
        {Op::scan, 1, 1, 10, 10, 10, 0, {"x&copy;y", 8}, {}},
    }},
    {{"jsx attribute \"unterminated", 27}, {"\"unterminated", 13}, true, true, {
        {Op::scan, 11, 11, 0, 13, 0, 4, {"unterminated", 12}, {{13, 13}}},
        {Op::rescanAttr, 11, 11, 0, 13, 0, 4, {"unterminated", 12}, {{13, 13}}},
        {Op::scan, 1, 1, 13, 13, 13, 0, {"unterminated", 12}, {}},
    }},
    {{"jsx attribute \"a\\\"b\"", 20}, {"\"a\\\"b\"", 6}, true, true, {
        {Op::scan, 11, 11, 0, 6, 0, 0, {"a\"b", 3}, {}},
        {Op::rescanAttr, 11, 11, 0, 4, 0, 0, {"a\\", 2}, {}},
        {Op::scan, 80, 80, 4, 5, 4, 0, {"b", 1}, {}},
        {Op::scan, 11, 11, 5, 6, 5, 4, {"", 0}, {{6, 6}}},
    }},
    {{"jsx attribute {name}", 20}, {"{name}", 6}, true, true, {
        {Op::scan, 19, 19, 0, 1, 0, 0, {"", 0}, {}},
        {Op::rescanAttr, 19, 19, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 1, 5, 1, 0, {"name", 4}, {}},
        {Op::scan, 20, 20, 5, 6, 5, 0, {"name", 4}, {}},
    }},
    {{"jsx attribute =\"hello\"", 22}, {"=\"hello\"", 8}, true, true, {
        {Op::scan, 64, 64, 0, 1, 0, 0, {"", 0}, {}},
        {Op::rescanAttr, 64, 64, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 11, 11, 1, 8, 1, 0, {"hello", 5}, {}},
        {Op::scan, 1, 1, 8, 8, 8, 0, {"hello", 5}, {}},
    }},
    {{"jsx direct attribute", 20}, {"=\"hello\" +x", 11}, true, true, {
        {Op::scan, 64, 64, 0, 1, 0, 0, {"", 0}, {}},
        {Op::attr, 11, 11, 0, 8, 1, 0, {"hello", 5}, {}},
        {Op::scan, 40, 40, 9, 10, 8, 0, {"hello", 5}, {}},
        {Op::scan, 80, 80, 10, 11, 10, 0, {"x", 1}, {}},
    }},
    {{"jsx normal </x>", 15}, {"</x>", 4}, false, true, {
        {Op::scan, 31, 31, 0, 2, 0, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 2, 3, 2, 0, {"x", 1}, {}},
        {Op::scan, 32, 32, 3, 4, 3, 0, {"x", 1}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"x", 1}, {}},
    }},
    {{"jsx normal </* comment */ x", 27}, {"</* comment */ x", 16}, false, true, {
        {Op::scan, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 3, 3, 1, 14, 1, 0, {"", 0}, {}},
        {Op::scan, 5, 5, 14, 15, 14, 0, {"", 0}, {}},
        {Op::scan, 80, 80, 15, 16, 15, 0, {"x", 1}, {}},
        {Op::scan, 1, 1, 16, 16, 16, 0, {"x", 1}, {}},
    }},
    {{"jsx normal <>", 13}, {"<>", 2}, false, true, {
        {Op::scan, 30, 30, 0, 1, 0, 0, {"", 0}, {}},
        {Op::scan, 32, 32, 1, 2, 1, 0, {"", 0}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"", 0}, {}},
    }},
    {{"jsx normal a</b>", 16}, {"a</b>", 5}, false, true, {
        {Op::scan, 80, 80, 0, 1, 0, 0, {"a", 1}, {}},
        {Op::scan, 31, 31, 1, 3, 1, 0, {"a", 1}, {}},
        {Op::scan, 80, 80, 3, 4, 3, 0, {"b", 1}, {}},
        {Op::scan, 32, 32, 4, 5, 4, 0, {"b", 1}, {}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"b", 1}, {}},
    }},
    {{"jsdoc @param {string} user-name - description\015\012", 47}, {"@param {string} user-name - description\015\012", 41}, false, false, {
        {Op::jsdoc, 60, 60, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdoc, 80, 80, 1, 6, 1, 0, {"param", 5}, {}},
        {Op::jsdoc, 5, 5, 6, 7, 6, 0, {"param", 5}, {}},
        {Op::jsdoc, 19, 19, 7, 8, 7, 0, {"param", 5}, {}},
        {Op::jsdoc, 154, 154, 8, 14, 8, 0, {"string", 6}, {}},
        {Op::jsdoc, 20, 20, 14, 15, 14, 0, {"string", 6}, {}},
        {Op::jsdoc, 5, 5, 15, 16, 15, 0, {"string", 6}, {}},
        {Op::jsdoc, 80, 80, 16, 25, 16, 0, {"user-name", 9}, {}},
        {Op::jsdoc, 5, 5, 25, 26, 25, 0, {"user-name", 9}, {}},
        {Op::jsdoc, 0, 0, 26, 27, 26, 0, {"user-name", 9}, {}},
        {Op::jsdoc, 5, 5, 27, 28, 27, 0, {"user-name", 9}, {}},
        {Op::jsdoc, 80, 80, 28, 39, 28, 0, {"description", 11}, {}},
        {Op::jsdoc, 4, 4, 39, 41, 39, 1, {"description", 11}, {}},
        {Op::jsdoc, 1, 1, 41, 41, 41, 0, {"description", 11}, {}},
    }},
    {{"jsdoc @template T,U", 19}, {"@template T,U", 13}, false, false, {
        {Op::jsdoc, 60, 60, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdoc, 80, 80, 1, 9, 1, 0, {"template", 8}, {}},
        {Op::jsdoc, 5, 5, 9, 10, 9, 0, {"template", 8}, {}},
        {Op::jsdoc, 80, 80, 10, 11, 10, 0, {"T", 1}, {}},
        {Op::jsdoc, 28, 28, 11, 12, 11, 0, {"T", 1}, {}},
        {Op::jsdoc, 80, 80, 12, 13, 12, 0, {"U", 1}, {}},
        {Op::jsdoc, 1, 1, 13, 13, 13, 0, {"U", 1}, {}},
    }},
    {{"jsdoc `code` #method [x] (y)", 28}, {"`code` #method [x] (y)", 22}, false, false, {
        {Op::jsdoc, 62, 62, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdoc, 80, 80, 1, 5, 1, 0, {"code", 4}, {}},
        {Op::jsdoc, 62, 62, 5, 6, 5, 0, {"code", 4}, {}},
        {Op::jsdoc, 5, 5, 6, 7, 6, 0, {"code", 4}, {}},
        {Op::jsdoc, 63, 63, 7, 8, 7, 0, {"code", 4}, {}},
        {Op::jsdoc, 80, 80, 8, 14, 8, 0, {"method", 6}, {}},
        {Op::jsdoc, 5, 5, 14, 15, 14, 0, {"method", 6}, {}},
        {Op::jsdoc, 23, 23, 15, 16, 15, 0, {"method", 6}, {}},
        {Op::jsdoc, 80, 80, 16, 17, 16, 0, {"x", 1}, {}},
        {Op::jsdoc, 24, 24, 17, 18, 17, 0, {"x", 1}, {}},
        {Op::jsdoc, 5, 5, 18, 19, 18, 0, {"x", 1}, {}},
        {Op::jsdoc, 21, 21, 19, 20, 19, 0, {"x", 1}, {}},
        {Op::jsdoc, 80, 80, 20, 21, 20, 0, {"y", 1}, {}},
        {Op::jsdoc, 22, 22, 21, 22, 21, 0, {"y", 1}, {}},
        {Op::jsdoc, 1, 1, 22, 22, 22, 0, {"y", 1}, {}},
    }},
    {{"jsdoc \\u0061 \\u{1F600}", 22}, {"\\u0061 \\u{1F600}", 16}, false, false, {
        {Op::jsdoc, 80, 80, 0, 6, 0, 1024, {"a", 1}, {}},
        {Op::jsdoc, 5, 5, 6, 7, 6, 0, {"a", 1}, {}},
        {Op::jsdoc, 0, 0, 7, 8, 7, 0, {"a", 1}, {}},
        {Op::jsdoc, 80, 80, 8, 9, 8, 0, {"u", 1}, {}},
        {Op::jsdoc, 19, 19, 9, 10, 9, 0, {"u", 1}, {}},
        {Op::jsdoc, 0, 0, 10, 11, 10, 0, {"u", 1}, {}},
        {Op::jsdoc, 80, 80, 11, 15, 11, 0, {"F600", 4}, {}},
        {Op::jsdoc, 20, 20, 15, 16, 15, 0, {"F600", 4}, {}},
        {Op::jsdoc, 1, 1, 16, 16, 16, 0, {"F600", 4}, {}},
    }},
    {{"jsdoc \345\217\230\351\207\217-name . ? ! : /", 27}, {"\345\217\230\351\207\217-name . ? ! : /", 21}, false, false, {
        {Op::jsdoc, 80, 80, 0, 7, 0, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 5, 5, 7, 8, 7, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 25, 25, 8, 9, 8, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 5, 5, 9, 10, 9, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 0, 0, 10, 11, 10, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 5, 5, 11, 12, 11, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 0, 0, 12, 13, 12, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 5, 5, 13, 14, 13, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 0, 0, 14, 15, 14, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 5, 5, 15, 16, 15, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 0, 0, 16, 17, 16, 0, {"\345\217\230\351\207\217-name", 11}, {}},
        {Op::jsdoc, 1, 1, 17, 17, 17, 0, {"\345\217\230\351\207\217-name", 11}, {}},
    }},
    {{"jsdoc  \302\205text", 13}, {" \302\205text", 7}, false, false, {
        {Op::jsdoc, 5, 5, 0, 2, 0, 0, {"", 0}, {}},
        {Op::jsdoc, 80, 80, 2, 6, 2, 0, {"text", 4}, {}},
        {Op::jsdoc, 1, 1, 6, 6, 6, 0, {"text", 4}, {}},
    }},
    {{"jsdoc @returns\012* {Foo}", 22}, {"@returns\012* {Foo}", 16}, false, false, {
        {Op::jsdoc, 60, 60, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdoc, 80, 80, 1, 8, 1, 0, {"returns", 7}, {}},
        {Op::jsdoc, 4, 4, 8, 9, 8, 1, {"returns", 7}, {}},
        {Op::jsdoc, 42, 42, 9, 10, 9, 0, {"returns", 7}, {}},
        {Op::jsdoc, 5, 5, 10, 11, 10, 0, {"returns", 7}, {}},
        {Op::jsdoc, 19, 19, 11, 12, 11, 0, {"returns", 7}, {}},
        {Op::jsdoc, 80, 80, 12, 15, 12, 0, {"Foo", 3}, {}},
        {Op::jsdoc, 20, 20, 15, 16, 15, 0, {"Foo", 3}, {}},
        {Op::jsdoc, 1, 1, 16, 16, 16, 0, {"Foo", 3}, {}},
    }},
    {{"jsdoc text description @param value", 35}, {"description @param value", 24}, false, false, {
        {Op::jsdocText, 82, 82, 0, 12, 0, 0, {"description ", 12}, {}},
        {Op::jsdocText, 60, 60, 12, 13, 12, 0, {"description ", 12}, {}},
        {Op::jsdocText, 82, 82, 13, 24, 13, 0, {"param value", 11}, {}},
        {Op::jsdocText, 1, 1, 24, 24, 24, 0, {"param value", 11}, {}},
    }},
    {{"jsdoc code description @param value", 35}, {"description @param value", 24}, false, false, {
        {Op::jsdocCode, 82, 82, 0, 24, 0, 0, {"description @param value", 24}, {}},
        {Op::jsdocCode, 1, 1, 24, 24, 24, 0, {"description @param value", 24}, {}},
    }},
    {{"jsdoc text mail a@b.com inline", 30}, {"mail a@b.com inline", 19}, false, false, {
        {Op::jsdocText, 82, 82, 0, 19, 0, 0, {"mail a@b.com inline", 19}, {}},
        {Op::jsdocText, 1, 1, 19, 19, 19, 0, {"mail a@b.com inline", 19}, {}},
    }},
    {{"jsdoc code mail a@b.com inline", 30}, {"mail a@b.com inline", 19}, false, false, {
        {Op::jsdocCode, 82, 82, 0, 19, 0, 0, {"mail a@b.com inline", 19}, {}},
        {Op::jsdocCode, 1, 1, 19, 19, 19, 0, {"mail a@b.com inline", 19}, {}},
    }},
    {{"jsdoc text {@link Name} @see Other", 34}, {"{@link Name} @see Other", 23}, false, false, {
        {Op::jsdocText, 19, 19, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdocText, 82, 82, 1, 13, 1, 0, {"@link Name} ", 12}, {}},
        {Op::jsdocText, 60, 60, 13, 14, 13, 0, {"@link Name} ", 12}, {}},
        {Op::jsdocText, 82, 82, 14, 23, 14, 0, {"see Other", 9}, {}},
        {Op::jsdocText, 1, 1, 23, 23, 23, 0, {"see Other", 9}, {}},
    }},
    {{"jsdoc code {@link Name} @see Other", 34}, {"{@link Name} @see Other", 23}, false, false, {
        {Op::jsdocCode, 82, 82, 0, 23, 0, 0, {"{@link Name} @see Other", 23}, {}},
        {Op::jsdocCode, 1, 1, 23, 23, 23, 0, {"{@link Name} @see Other", 23}, {}},
    }},
    {{"jsdoc text `a@b {c}`\012next", 25}, {"`a@b {c}`\012next", 14}, false, false, {
        {Op::jsdocText, 62, 62, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdocText, 82, 82, 1, 5, 1, 0, {"a@b ", 4}, {}},
        {Op::jsdocText, 19, 19, 5, 6, 5, 0, {"a@b ", 4}, {}},
        {Op::jsdocText, 82, 82, 6, 8, 6, 0, {"c}", 2}, {}},
        {Op::jsdocText, 62, 62, 8, 9, 8, 0, {"c}", 2}, {}},
        {Op::jsdocText, 4, 4, 9, 10, 9, 1, {"c}", 2}, {}},
        {Op::jsdocText, 82, 82, 10, 14, 10, 0, {"next", 4}, {}},
        {Op::jsdocText, 1, 1, 14, 14, 14, 0, {"next", 4}, {}},
    }},
    {{"jsdoc code `a@b {c}`\012next", 25}, {"`a@b {c}`\012next", 14}, false, false, {
        {Op::jsdocCode, 62, 62, 0, 1, 0, 0, {"", 0}, {}},
        {Op::jsdocCode, 82, 82, 1, 8, 1, 0, {"a@b {c}", 7}, {}},
        {Op::jsdocCode, 62, 62, 8, 9, 8, 0, {"a@b {c}", 7}, {}},
        {Op::jsdocCode, 4, 4, 9, 10, 9, 1, {"a@b {c}", 7}, {}},
        {Op::jsdocCode, 82, 82, 10, 14, 10, 0, {"next", 4}, {}},
        {Op::jsdocCode, 1, 1, 14, 14, 14, 0, {"next", 4}, {}},
    }},
    {{"jsdoc text text @ space", 23}, {"text @ space", 12}, false, false, {
        {Op::jsdocText, 82, 82, 0, 12, 0, 0, {"text @ space", 12}, {}},
        {Op::jsdocText, 1, 1, 12, 12, 12, 0, {"text @ space", 12}, {}},
    }},
    {{"jsdoc code text @ space", 23}, {"text @ space", 12}, false, false, {
        {Op::jsdocCode, 82, 82, 0, 12, 0, 0, {"text @ space", 12}, {}},
        {Op::jsdocCode, 1, 1, 12, 12, 12, 0, {"text @ space", 12}, {}},
    }},
    {{"jsdoc text \360\237\230\200text \012 end", 25}, {"\360\237\230\200text \012 end", 14}, false, false, {
        {Op::jsdocText, 82, 82, 0, 7, 0, 0, {"\360\237\230\200text ", 9}, {}},
        {Op::jsdocText, 4, 4, 7, 8, 7, 1, {"\360\237\230\200text ", 9}, {}},
        {Op::jsdocText, 82, 82, 8, 12, 8, 0, {" end", 4}, {}},
        {Op::jsdocText, 1, 1, 12, 12, 12, 0, {" end", 4}, {}},
    }},
    {{"jsdoc code \360\237\230\200text \012 end", 25}, {"\360\237\230\200text \012 end", 14}, false, false, {
        {Op::jsdocCode, 82, 82, 0, 7, 0, 0, {"\360\237\230\200text ", 9}, {}},
        {Op::jsdocCode, 4, 4, 7, 8, 7, 1, {"\360\237\230\200text ", 9}, {}},
        {Op::jsdocCode, 82, 82, 8, 12, 8, 0, {" end", 4}, {}},
        {Op::jsdocCode, 1, 1, 12, 12, 12, 0, {" end", 4}, {}},
    }},
    {{"conflict <<<<<<< HEAD\012let a=1;\012=======\012let b=2;\012>>>>>>> branch\012", 63}, {"<<<<<<< HEAD\012let a=1;\012=======\012let b=2;\012>>>>>>> branch\012", 54}, false, false, {
        {Op::scan, 7, 7, 0, 12, 0, 0, {"", 0}, {{0, 7}}},
        {Op::scan, 4, 4, 12, 13, 12, 1, {"", 0}, {}},
        {Op::scan, 121, 121, 13, 16, 13, 0, {"let", 3}, {}},
        {Op::scan, 5, 5, 16, 17, 16, 0, {"let", 3}, {}},
        {Op::scan, 80, 80, 17, 18, 17, 0, {"a", 1}, {}},
        {Op::scan, 64, 64, 18, 19, 18, 0, {"a", 1}, {}},
        {Op::scan, 9, 9, 19, 20, 19, 0, {"1", 1}, {}},
        {Op::scan, 27, 27, 20, 21, 20, 0, {"1", 1}, {}},
        {Op::scan, 4, 4, 21, 22, 21, 1, {"1", 1}, {}},
        {Op::scan, 7, 7, 22, 39, 22, 0, {"1", 1}, {{22, 29}}},
        {Op::scan, 7, 7, 39, 53, 39, 0, {"1", 1}, {{39, 46}}},
        {Op::scan, 4, 4, 53, 54, 53, 1, {"1", 1}, {}},
        {Op::scan, 1, 1, 54, 54, 54, 0, {"1", 1}, {}},
    }},
    {{"conflict skip <<<<<<< HEAD\012let a=1;\012=======\012let b=2;\012>>>>>>> branch\012", 68}, {"<<<<<<< HEAD\012let a=1;\012=======\012let b=2;\012>>>>>>> branch\012", 54}, true, false, {
        {Op::scan, 121, 121, 13, 16, 0, 1, {"let", 3}, {{0, 7}}},
        {Op::scan, 80, 80, 17, 18, 16, 0, {"a", 1}, {}},
        {Op::scan, 64, 64, 18, 19, 18, 0, {"a", 1}, {}},
        {Op::scan, 9, 9, 19, 20, 19, 0, {"1", 1}, {}},
        {Op::scan, 27, 27, 20, 21, 20, 0, {"1", 1}, {}},
        {Op::scan, 1, 1, 54, 54, 21, 1, {"1", 1}, {{22, 29}, {39, 46}}},
    }},
    {{"conflict ||||||| base\012base\012=======\012other\012>>>>>>> b\012", 51}, {"||||||| base\012base\012=======\012other\012>>>>>>> b\012", 42}, false, false, {
        {Op::scan, 7, 7, 0, 18, 0, 0, {"", 0}, {{0, 7}}},
        {Op::scan, 7, 7, 18, 32, 18, 0, {"", 0}, {{18, 25}}},
        {Op::scan, 7, 7, 32, 41, 32, 0, {"", 0}, {{32, 39}}},
        {Op::scan, 4, 4, 41, 42, 41, 1, {"", 0}, {}},
        {Op::scan, 1, 1, 42, 42, 42, 0, {"", 0}, {}},
    }},
    {{"conflict skip ||||||| base\012base\012=======\012other\012>>>>>>> b\012", 56}, {"||||||| base\012base\012=======\012other\012>>>>>>> b\012", 42}, true, false, {
        {Op::scan, 1, 1, 42, 42, 0, 1, {"", 0}, {{0, 7}, {18, 25}, {32, 39}}},
    }},
    {{"conflict x <<<<<<< not-a-marker\012", 32}, {"x <<<<<<< not-a-marker\012", 23}, false, false, {
        {Op::scan, 80, 80, 0, 1, 0, 0, {"x", 1}, {}},
        {Op::scan, 5, 5, 1, 2, 1, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 2, 4, 2, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 4, 6, 4, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 6, 8, 6, 0, {"x", 1}, {}},
        {Op::scan, 30, 30, 8, 9, 8, 0, {"x", 1}, {}},
        {Op::scan, 5, 5, 9, 10, 9, 0, {"x", 1}, {}},
        {Op::scan, 80, 80, 10, 13, 10, 0, {"not", 3}, {}},
        {Op::scan, 41, 41, 13, 14, 13, 0, {"not", 3}, {}},
        {Op::scan, 80, 80, 14, 15, 14, 0, {"a", 1}, {}},
        {Op::scan, 41, 41, 15, 16, 15, 0, {"a", 1}, {}},
        {Op::scan, 80, 80, 16, 22, 16, 0, {"marker", 6}, {}},
        {Op::scan, 4, 4, 22, 23, 22, 1, {"marker", 6}, {}},
        {Op::scan, 1, 1, 23, 23, 23, 0, {"marker", 6}, {}},
    }},
    {{"conflict skip x <<<<<<< not-a-marker\012", 37}, {"x <<<<<<< not-a-marker\012", 23}, true, false, {
        {Op::scan, 80, 80, 0, 1, 0, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 2, 4, 1, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 4, 6, 4, 0, {"x", 1}, {}},
        {Op::scan, 48, 48, 6, 8, 6, 0, {"x", 1}, {}},
        {Op::scan, 30, 30, 8, 9, 8, 0, {"x", 1}, {}},
        {Op::scan, 80, 80, 10, 13, 9, 0, {"not", 3}, {}},
        {Op::scan, 41, 41, 13, 14, 13, 0, {"not", 3}, {}},
        {Op::scan, 80, 80, 14, 15, 14, 0, {"a", 1}, {}},
        {Op::scan, 41, 41, 15, 16, 15, 0, {"a", 1}, {}},
        {Op::scan, 80, 80, 16, 22, 16, 0, {"marker", 6}, {}},
        {Op::scan, 1, 1, 23, 23, 22, 1, {"marker", 6}, {}},
    }},
    {{"conflict =======\012ignored\012", 25}, {"=======\012ignored\012", 16}, false, false, {
        {Op::scan, 7, 7, 0, 16, 0, 0, {"", 0}, {{0, 7}}},
        {Op::scan, 1, 1, 16, 16, 16, 0, {"", 0}, {}},
    }},
    {{"conflict skip =======\012ignored\012", 30}, {"=======\012ignored\012", 16}, true, false, {
        {Op::scan, 1, 1, 16, 16, 0, 0, {"", 0}, {{0, 7}}},
    }},
    {{"radix/space 0x", 14}, {"0x", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"x", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"x", 1}, {}},
    }},
    {{"radix/space 0X", 14}, {"0X", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"X", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"X", 1}, {}},
    }},
    {{"radix/space 0b", 14}, {"0b", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"b", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"b", 1}, {}},
    }},
    {{"radix/space 0B", 14}, {"0B", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"B", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"B", 1}, {}},
    }},
    {{"radix/space 0o", 14}, {"0o", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"o", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"o", 1}, {}},
    }},
    {{"radix/space 0O", 14}, {"0O", 2}, false, false, {
        {Op::scan, 9, 9, 0, 1, 0, 0, {"0", 1}, {{1, 2}}},
        {Op::scan, 80, 80, 1, 2, 1, 0, {"O", 1}, {}},
        {Op::scan, 1, 1, 2, 2, 2, 0, {"O", 1}, {}},
    }},
    {{"radix/space 0x; 0b2; 0o8;", 25}, {"0x; 0b2; 0o8;", 13}, false, false, {
        {Op::scan, 9, 9, 0, 2, 0, 64, {"0", 1}, {{2, 2}}},
        {Op::scan, 27, 27, 2, 3, 2, 0, {"0", 1}, {}},
        {Op::scan, 5, 5, 3, 4, 3, 0, {"0", 1}, {}},
        {Op::scan, 9, 9, 4, 6, 4, 128, {"0", 1}, {{6, 6}}},
        {Op::scan, 9, 9, 6, 7, 6, 0, {"2", 1}, {}},
        {Op::scan, 27, 27, 7, 8, 7, 0, {"2", 1}, {}},
        {Op::scan, 5, 5, 8, 9, 8, 0, {"2", 1}, {}},
        {Op::scan, 9, 9, 9, 11, 9, 256, {"0", 1}, {{11, 11}}},
        {Op::scan, 9, 9, 11, 12, 11, 0, {"8", 1}, {}},
        {Op::scan, 27, 27, 12, 13, 12, 0, {"8", 1}, {}},
        {Op::scan, 1, 1, 13, 13, 13, 0, {"8", 1}, {}},
    }},
    {{"radix/space \302\205foo", 17}, {"\302\205foo", 5}, false, false, {
        {Op::scan, 80, 80, 1, 4, 0, 0, {"foo", 3}, {}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"foo", 3}, {}},
    }},
    {{"radix/space let \302\205x = 1;", 24}, {"let \302\205x = 1;", 12}, false, false, {
        {Op::scan, 121, 121, 0, 3, 0, 0, {"let", 3}, {}},
        {Op::scan, 5, 5, 3, 5, 3, 0, {"let", 3}, {}},
        {Op::scan, 80, 80, 5, 6, 5, 0, {"x", 1}, {}},
        {Op::scan, 5, 5, 6, 7, 6, 0, {"x", 1}, {}},
        {Op::scan, 64, 64, 7, 8, 7, 0, {"x", 1}, {}},
        {Op::scan, 5, 5, 8, 9, 8, 0, {"x", 1}, {}},
        {Op::scan, 9, 9, 9, 10, 9, 0, {"1", 1}, {}},
        {Op::scan, 27, 27, 10, 11, 10, 0, {"1", 1}, {}},
        {Op::scan, 1, 1, 11, 11, 11, 0, {"1", 1}, {}},
    }},
    {{"radix/space 0x_FF", 17}, {"0x_FF", 5}, false, false, {
        {Op::scan, 9, 9, 0, 5, 0, 576, {"255", 3}, {{2, 3}}},
        {Op::scan, 1, 1, 5, 5, 5, 0, {"255", 3}, {}},
    }},
    {{"radix/space 0b_1", 16}, {"0b_1", 4}, false, false, {
        {Op::scan, 9, 9, 0, 4, 0, 640, {"1", 1}, {{2, 3}}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"1", 1}, {}},
    }},
    {{"radix/space 0o_7", 16}, {"0o_7", 4}, false, false, {
        {Op::scan, 9, 9, 0, 4, 0, 768, {"7", 1}, {{2, 3}}},
        {Op::scan, 1, 1, 4, 4, 4, 0, {"7", 1}, {}},
    }},
};
Token apply(Scanner& scanner, Op op) {
    switch (op) {
    case Op::scan: return scanner.scan();
    case Op::greater: return scanner.rescan_greater();
    case Op::less: return scanner.rescan_less_than();
    case Op::hash: return scanner.rescan_hash();
    case Op::question: return scanner.rescan_question();
    case Op::asterisk: return scanner.rescan_asterisk_equals();
    case Op::slash: return scanner.rescan_slash();
    case Op::templateToken: return scanner.rescan_template(false);
    case Op::tagged: return scanner.rescan_template(true);
    case Op::jsx: return scanner.scan_jsx_token(true);
    case Op::jsxSingle: return scanner.scan_jsx_token(false);
    case Op::rescanJsx: return scanner.rescan_jsx_token(true);
    case Op::jsxIdentifier: return scanner.scan_jsx_identifier();
    case Op::attr: return scanner.scan_jsx_attribute_value();
    case Op::rescanAttr: return scanner.rescan_jsx_attribute_value();
    case Op::jsdoc: return scanner.scan_jsdoc_token();
    case Op::jsdocText: return scanner.scan_jsdoc_comment_text(false);
    case Op::jsdocCode: return scanner.scan_jsdoc_comment_text(true);
    }
    throw std::logic_error("Unknown scanner operation.");
}
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
bool equal_state(const Scanner& scanner, const Token& value, size_t full) {
    const auto now = scanner.current();
    return now.kind == value.kind && now.start == value.start && now.end == value.end &&
        now.flags == value.flags && now.value == value.value && scanner.full_start == full;
}
// Host adaptations: C++ exceptions restore state; invalid ranges fail explicitly.
// The bounded trailing-hyphen check deliberately avoids an old upstream hang.
void host_contracts() {
    Text text("alpha beta gamma"); Scanner scanner(text, true);
    scanner.scan(); const auto first = scanner.current(); const auto full = scanner.full_start;
    auto preview = scanner.look_ahead([&] { return scanner.scan(); });
    require(preview.value == "beta" && equal_state(scanner, first, full), "lookAhead must restore all token state");
    bool accepted = scanner.try_scan([&] { scanner.scan(); return false; });
    require(!accepted && equal_state(scanner, first, full), "failed tryScan must restore all state");
    accepted = scanner.try_scan([&] { return scanner.scan().value == "beta"; });
    require(accepted && scanner.current().value == "beta", "successful tryScan must commit");
    const auto second = scanner.current(); const auto secondFull = scanner.full_start;
    auto range = scanner.scan_range(11, 5, [&] { auto value = scanner.scan(); require(scanner.scan().kind == mn::typescript::sk::EndOfFileToken, "bounded range EOF"); return value; });
    require(range.value == "gamma" && equal_state(scanner, second, secondFull), "scanRange must restore outer state");
    scanner.scan_range(2, 0, [&] { require(scanner.scan().kind == mn::typescript::sk::EndOfFileToken, "empty range"); });
    require(equal_state(scanner, second, secondFull), "empty range restores outer state");
    try { scanner.look_ahead([&]() -> int { scanner.scan(); throw std::runtime_error("expected"); }); }
    catch (const std::runtime_error&) { }
    require(equal_state(scanner, second, secondFull), "lookAhead exception safety");
    try { scanner.try_scan([&]() -> bool { scanner.scan(); throw std::runtime_error("expected"); }); }
    catch (const std::runtime_error&) { }
    require(equal_state(scanner, second, secondFull), "tryScan exception safety");
    try { scanner.scan_range(0, 1, [&]() -> int { scanner.scan(); throw std::runtime_error("expected"); }); }
    catch (const std::runtime_error&) { }
    require(equal_state(scanner, second, secondFull), "scanRange exception safety");
    for (const auto& [offset, length] : {std::pair<size_t, size_t>{100, 1}, {0, 100}, {0, static_cast<size_t>(-1)}}) {
        bool caught = false; try { scanner.scan_range(offset, length, [] { return true; }); } catch (const std::out_of_range&) { caught = true; }
        require(caught && equal_state(scanner, second, secondFull), "invalid range must not mutate state");
    }
    scanner.reset_token_state(0);
    require(scanner.pos == 0 && scanner.kind == 0 && scanner.flags == 0 && scanner.token_value.empty(), "resetTokenState");
    Text trailing("some-name-"); Scanner doc(trailing);
    require(doc.scan_jsdoc_token().value == "some-name-", "trailing JSDoc hyphen must terminate");
    require(doc.scan_jsdoc_token().kind == mn::typescript::sk::EndOfFileToken, "trailing JSDoc EOF");
    Text diagnostic("a \\x"); Scanner withError(diagnostic, true); withError.scan();
    auto initial = withError.current(); auto initialFull = withError.full_start;
    withError.look_ahead([&] { withError.scan(); return true; });
    require(!withError.errors.empty() && equal_state(withError, initial, initialFull), "diagnostics retained across speculative restore");
}
}
void TypeScriptScannerApiFixtures() {
    size_t failures = 0, states = 0;
    for (const auto& fixture : fixtures) {
        Text text(fixture.source); Scanner scanner(text, fixture.skip, fixture.jsx);
        for (size_t i = 0; i < fixture.states.size(); ++i) {
            const auto& expected = fixture.states[i]; const auto errorStart = scanner.errors.size();
            const auto actual = apply(scanner, expected.op); ++states;
            bool matches = actual.kind == expected.returned && scanner.kind == expected.stored &&
                actual.start == expected.start && actual.end == expected.end && scanner.full_start == expected.full &&
                actual.flags == expected.flags && actual.value == expected.value &&
                scanner.errors.size() - errorStart == expected.errors.size();
            if (scanner.errors.size() - errorStart == expected.errors.size())
                for (size_t e = 0; e < expected.errors.size(); ++e)
                    matches = matches && scanner.errors[errorStart + e].start == expected.errors[e].start && scanner.errors[errorStart + e].end == expected.errors[e].end;
            if (!matches) {
                ++failures;
                std::cerr << "[Scanner API mismatch] " << fixture.name << " step=" << i
                    << " kind=" << actual.kind << '/' << expected.returned << " stored=" << scanner.kind << '/' << expected.stored
                    << " start=" << actual.start << '/' << expected.start << " end=" << actual.end << '/' << expected.end
                    << " full=" << scanner.full_start << '/' << expected.full << " flags=" << actual.flags << '/' << expected.flags
                    << " value=" << mn::language::quote(actual.value) << '/' << mn::language::quote(expected.value)
                    << " errors=" << scanner.errors.size() - errorStart << '/' << expected.errors.size() << '\n';
                for(size_t e = errorStart; e < scanner.errors.size(); ++e) std::cerr << " actual error " << scanner.errors[e].start << ':' << scanner.errors[e].end << '\n';
                for(const auto& e : expected.errors) std::cerr << " expected error " << e.start << ':' << e.end << '\n';
            }
        }
    }
    if (failures) throw std::runtime_error(std::to_string(failures) + " scanner API differential mismatches");
    host_contracts();
    std::cout << "TypeScript scanner API: " << fixtures.size() << " fixtures, " << states << " states; host contracts passed\n";
}
