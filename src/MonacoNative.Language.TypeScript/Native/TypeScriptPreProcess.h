// Modified C++ translation of Microsoft TypeScript 5.8.3 services/preProcess.ts,
// and the reference-pragma branches of compiler/parser.ts. Copyright Microsoft.
// Apache-2.0; see THIRD_PARTY_NOTICES.md. This is NOT a parser/binder/checker.
#pragma once
#include "TypeScriptScanner.h"
#include <optional>

namespace mn::typescript
{
struct FileReference
{
    std::string fileName;
    size_t pos = 0, end = 0; // Upstream zero-based UTF-16 offsets, NOT UTF-8 byte offsets.
    int resolutionMode = 0; // 0 = absent, 1 = CommonJS, 99 = ESNext.
    bool preserve = false;
    bool operator==(const FileReference&) const = default;
};
enum class FileReferenceKind { Import, Path, Types, Lib };
struct LocatedFileReference
{
    FileReference reference;
    FileReferenceKind kind = FileReferenceKind::Import;
    size_t contentStart = 0, contentEnd = 0;
    // Actual raw-literal content span for host edits/navigation. Upstream imported
    // FileReference.end uses COOKED length from the OPENING quote; it is not an
    // edit range, especially with escapes. Keep these representations separate.
};
struct PreProcessedFileInfo
{
    std::vector<FileReference> referencedFiles, typeReferenceDirectives, libReferenceDirectives, importedFiles;
    bool isLibFile = false;
    std::optional<std::vector<std::string>> ambientExternalModules;
    std::vector<LocatedFileReference> locations; // Host-only; omitted from upstream result JSON.
};
inline std::string reference_json(const FileReference& ref)
{
    using namespace mn::language;
    auto result = "{\"fileName\":" + quote(ref.fileName) + ",\"pos\":" + std::to_string(ref.pos) + ",\"end\":" + std::to_string(ref.end);
    if (ref.resolutionMode) result += ",\"resolutionMode\":" + std::to_string(ref.resolutionMode);
    if (ref.preserve) result += ",\"preserve\":true";
    return result + '}';
}
inline std::string preprocess_json(const PreProcessedFileInfo& info)
{
    using namespace mn::language;
    auto refs = [](const std::vector<FileReference>& values)
    { std::string result = "["; for (const auto& ref : values) append(result, reference_json(ref)); return result + ']'; };
    auto result = "{\"referencedFiles\":" + refs(info.referencedFiles) + ",\"typeReferenceDirectives\":" + refs(info.typeReferenceDirectives) +
        ",\"libReferenceDirectives\":" + refs(info.libReferenceDirectives) + ",\"importedFiles\":" + refs(info.importedFiles) +
        ",\"isLibFile\":" + (info.isLibFile ? "true" : "false");
    if (info.ambientExternalModules)
    {
        std::string names = "[";
        for (const auto& name : *info.ambientExternalModules) append(names, quote(name));
        result += ",\"ambientExternalModules\":" + names + ']';
    }
    return result + '}';
}

class PreProcessor
{
    const mn::language::Text& source;
    Scanner scanner;
    PreProcessedFileInfo result;
    struct Ambient { LocatedFileReference location; int depth; };
    std::vector<Ambient> ambient;
    int lastToken = sk::Unknown, currentToken = sk::Unknown, braceNesting = 0;
    bool externalModule = false;
    size_t scanned = 0;
    Token scan()
    {
        if (++scanned > 4000000) throw std::length_error("TypeScript preprocessor token budget exceeded.");
        return scanner.scan();
    }
    int next_token()
    {
        lastToken = currentToken; currentToken = scan().kind;
        if (currentToken == sk::OpenBraceToken) ++braceNesting;
        else if (currentToken == sk::CloseBraceToken) --braceNesting;
        return currentToken;
    }
    static bool keyword(int token) { return token >= sk::BreakKeyword && token <= sk::OfKeyword; }
    static bool name_token(int token) { return token == sk::Identifier || keyword(token); }
    LocatedFileReference file_reference() const
    {
        const auto valueLength = mn::language::Text(scanner.token_value).utf16.size();
        const auto start = std::min(scanner.token_start + 1, scanner.pos);
        auto end = scanner.pos;
        if (!(scanner.flags & tf::Unterminated) && end > start &&
            source.utf16[end - 1] == source.utf16[scanner.token_start]) --end;
        return {{scanner.token_value, scanner.token_start, scanner.token_start + valueLength, 0, false}, FileReferenceKind::Import, start, end};
    }
    void mark_external() { if (braceNesting == 0) externalModule = true; }
    void record_module()
    {
        auto ref = file_reference(); result.importedFiles.push_back(ref.reference); result.locations.push_back(std::move(ref)); mark_external();
    }
    bool consume_declare()
    {
        if (scanner.kind != sk::DeclareKeyword) return false;
        if (next_token() == sk::ModuleKeyword && next_token() == sk::StringLiteral)
            ambient.push_back({file_reference(), braceNesting});
        return true;
    }
    bool consume_require(bool skipCurrent, bool allowTemplate = false)
    {
        const auto token = skipCurrent ? next_token() : scanner.kind;
        if (token != sk::RequireKeyword) return false;
        if (next_token() == sk::OpenParenToken)
        {
            const auto argument = next_token();
            if (argument == sk::StringLiteral || (allowTemplate && argument == sk::NoSubstitutionTemplateLiteral)) record_module();
        }
        return true;
    }
    bool consume_import()
    {
        if (lastToken == sk::DotToken || scanner.kind != sk::ImportKeyword) return false;
        auto token = next_token();
        if (token == sk::OpenParenToken)
        {
            token = next_token();
            if (token == sk::StringLiteral || token == sk::NoSubstitutionTemplateLiteral) { record_module(); return true; }
        }
        else if (token == sk::StringLiteral) { record_module(); return true; }
        else
        {
            if (token == sk::TypeKeyword && scanner.look_ahead([&]
                { const auto next = scan().kind; return next != sk::FromKeyword && (next == sk::AsteriskToken || next == sk::OpenBraceToken || name_token(next)); }))
                token = next_token();
            if (name_token(token))
            {
                token = next_token();
                if (token == sk::FromKeyword)
                { if (next_token() == sk::StringLiteral) { record_module(); return true; } }
                else if (token == sk::EqualsToken)
                { if (consume_require(true)) return true; }
                else if (token == sk::CommaToken) token = next_token();
                else return true;
            }
            if (token == sk::OpenBraceToken)
            {
                token = next_token();
                while (token != sk::CloseBraceToken && token != sk::EndOfFileToken) token = next_token();
                if (token == sk::CloseBraceToken && next_token() == sk::FromKeyword && next_token() == sk::StringLiteral) record_module();
            }
            else if (token == sk::AsteriskToken && next_token() == sk::AsKeyword && name_token(next_token()) &&
                next_token() == sk::FromKeyword && next_token() == sk::StringLiteral) record_module();
        }
        return true;
    }
    bool consume_export()
    {
        if (scanner.kind != sk::ExportKeyword) return false;
        mark_external(); auto token = next_token();
        if (token == sk::TypeKeyword && scanner.look_ahead([&]
            { const auto next = scan().kind; return next == sk::AsteriskToken || next == sk::OpenBraceToken; })) token = next_token();
        if (token == sk::OpenBraceToken)
        {
            token = next_token();
            while (token != sk::CloseBraceToken && token != sk::EndOfFileToken) token = next_token();
            if (token == sk::CloseBraceToken && next_token() == sk::FromKeyword && next_token() == sk::StringLiteral) record_module();
        }
        else if (token == sk::AsteriskToken)
        { if (next_token() == sk::FromKeyword && next_token() == sk::StringLiteral) record_module(); }
        else if (token == sk::ImportKeyword)
        {
            token = next_token();
            if (token == sk::TypeKeyword && scanner.look_ahead([&] { return name_token(scan().kind); })) token = next_token();
            if (name_token(token) && next_token() == sk::EqualsToken && consume_require(true)) return true;
        }
        return true;
    }
    bool consume_define()
    {
        if (scanner.kind != sk::Identifier || scanner.token_value != "define") return false;
        if (next_token() != sk::OpenParenToken) return true;
        auto token = next_token();
        if (token == sk::StringLiteral || token == sk::NoSubstitutionTemplateLiteral)
        { if (next_token() != sk::CommaToken) return true; token = next_token(); }
        if (token != sk::OpenBracketToken) return true;
        token = next_token();
        while (token != sk::CloseBracketToken && token != sk::EndOfFileToken)
        {
            if (token == sk::StringLiteral || token == sk::NoSubstitutionTemplateLiteral) record_module();
            token = next_token();
        }
        return true;
    }
    void process_imports(bool detectJavaScriptImports)
    {
        next_token();
        while (scanner.kind != sk::EndOfFileToken)
        {
            if (scanner.kind == sk::TemplateHead)
            {
                std::vector<int> stack{sk::TemplateHead};
                while (!stack.empty())
                {
                    const auto token = scan().kind;
                    if (token == sk::EndOfFileToken) break;
                    switch (token)
                    {
                    case sk::ImportKeyword: consume_import(); break;
                    case sk::TemplateHead: stack.push_back(token); break;
                    case sk::OpenBraceToken: if (!stack.empty()) stack.push_back(token); break;
                    case sk::CloseBraceToken:
                        if (!stack.empty())
                        {
                            if (stack.back() == sk::TemplateHead)
                            { if (scanner.rescan_template(false).kind == sk::TemplateTail) stack.pop_back(); }
                            else stack.pop_back();
                        }
                        break;
                    default: break;
                    }
                }
                next_token();
            }
            if (consume_declare() || consume_import() || consume_export() ||
                (detectJavaScriptImports && (consume_require(false, true) || consume_define()))) continue;
            next_token();
        }
    }
    // ECMAScript RegExp \s, deliberately not C locale whitespace or the TS
    // scanner's wider whitespace set (NEL/zero-width-space differ).
    static bool regex_space(int c)
    {
        return c == 9 || c == 10 || c == 11 || c == 12 || c == 13 || c == 32 || c == 160 || c == 0x1680 ||
            (c >= 0x2000 && c <= 0x200a) || c == 0x2028 || c == 0x2029 || c == 0x202f || c == 0x205f || c == 0x3000 || c == 0xfeff;
    }
    static int ascii_lower(int c) { return c >= 'A' && c <= 'Z' ? c + 32 : c; }
    struct PragmaArgument { std::string value; size_t start, end; };
    std::optional<PragmaArgument> argument(size_t start, size_t end, std::string_view name) const
    {
        const auto& text = source.utf16;
        // Translation of getNamedArgRegEx: first matching whitespace-delimited
        // name=value anywhere in the comment, not an XML parser interpretation.
        for (auto i = start; i < end; ++i)
        {
            if (!regex_space(text[i])) continue;
            auto p = i + 1; size_t n = 0;
            while (p < end && n < name.size() && ascii_lower(text[p]) == name[n]) { ++p; ++n; }
            if (n != name.size()) continue;
            while (p < end && regex_space(text[p])) ++p;
            if (p == end || text[p++] != '=') continue;
            while (p < end && regex_space(text[p])) ++p;
            if (p == end || (text[p] != '\'' && text[p] != '"')) continue;
            const auto quote = text[p++]; const auto valueStart = p;
            while (p < end && text[p] != quote) ++p;
            if (p == end) continue;
            // The upstream JS has a TypeError for empty single-quoted capture
            // spans. Return a normal empty filename rather than crash the host.
            return PragmaArgument{source.slice(valueStart, p), valueStart, p};
        }
        return std::nullopt;
    }
    bool reference_comment(size_t start, size_t end) const
    {
        const auto& text = source.utf16;
        if (end - start < 3 || text[start] != '/' || text[start + 1] != '/' || text[start + 2] != '/') return false;
        auto p = start + 3;
        while (p < end && regex_space(text[p])) ++p;
        if (p == end || text[p++] != '<') return false;
        const auto nameStart = p;
        while (p < end && !regex_space(text[p])) ++p;
        if (p == end || p - nameStart != 9) return false;
        constexpr std::string_view name = "reference";
        for (size_t i = 0; i < name.size(); ++i) if (ascii_lower(text[nameStart + i]) != name[i]) return false;
        ++p; // the mandatory whitespace after the tag name
        for (; p + 1 < end && !line_break(text[p]); ++p) if (text[p] == '/' && text[p + 1] == '>') return true;
        return false;
    }
    void process_pragmas()
    {
        Scanner comments(source);
        for (;;)
        {
            const auto token = comments.scan();
            if (token.kind == sk::WhitespaceTrivia || token.kind == sk::NewLineTrivia || token.kind == sk::ShebangTrivia || token.kind == sk::MultiLineCommentTrivia) continue;
            if (token.kind != sk::SingleLineCommentTrivia) break;
            if (!reference_comment(token.start, token.end)) continue;
            const auto noDefault = argument(token.start, token.end, "no-default-lib");
            if (noDefault && noDefault->value == "true") { result.isLibFile = true; continue; }
            const auto preserve = argument(token.start, token.end, "preserve");
            auto value = argument(token.start, token.end, "types"); auto kind = FileReferenceKind::Types;
            if (!value) { value = argument(token.start, token.end, "lib"); kind = FileReferenceKind::Lib; }
            if (!value) { value = argument(token.start, token.end, "path"); kind = FileReferenceKind::Path; }
            if (!value) continue; // Upstream ignores pragma diagnostics in preProcessFile.
            FileReference ref{value->value, value->start, value->end, 0, preserve && preserve->value == "true"};
            if (kind == FileReferenceKind::Types)
            {
                const auto mode = argument(token.start, token.end, "resolution-mode");
                if (mode) ref.resolutionMode = mode->value == "import" ? 99 : mode->value == "require" ? 1 : 0;
                result.typeReferenceDirectives.push_back(ref);
            }
            else if (kind == FileReferenceKind::Lib) result.libReferenceDirectives.push_back(ref);
            else result.referencedFiles.push_back(ref);
            result.locations.push_back({std::move(ref), kind, value->start, value->end});
        }
    }
public:
    explicit PreProcessor(const mn::language::Text& text) : source(text), scanner(text, true) {}
    PreProcessedFileInfo run(bool readImportFiles = true, bool detectJavaScriptImports = false)
    {
        if (readImportFiles) process_imports(detectJavaScriptImports);
        process_pragmas();
        for (auto& decl : ambient)
        {
            if (externalModule || decl.depth != 0)
            { result.importedFiles.push_back(decl.location.reference); result.locations.push_back(std::move(decl.location)); }
            else
            {
                if (!result.ambientExternalModules) result.ambientExternalModules.emplace();
                result.ambientExternalModules->push_back(decl.location.reference.fileName);
            }
        }
        return std::move(result);
    }
};
inline PreProcessedFileInfo pre_process_file(const mn::language::Text& text, bool readImportFiles = true, bool detectJavaScriptImports = false)
{ return PreProcessor(text).run(readImportFiles, detectJavaScriptImports); }
inline bool javascript_path(std::string_view path)
{
    auto dot = path.find_last_of('.'); if (dot == std::string_view::npos) return false;
    std::string extension(path.substr(dot));
    for (auto& c : extension) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + 32);
    return extension == ".js" || extension == ".jsx" || extension == ".mjs" || extension == ".cjs";
}
}
