// In-process libclang integration. This adapter is project code, not a claim to
// have ported the entire Clang/clangd implementation. ABI declarations follow
// llvmorg-17.0.6 clang/include/clang-c/{Index,CXString,CXSourceLocation}.h.
// Those upstream declarations are Apache-2.0 WITH LLVM-exception; see THIRD_PARTY_NOTICES.md.
#include "NativeLanguageSupport.h"
#include <filesystem>
#include <memory>
#include <mutex>
#include <sstream>
#include <iomanip>
#include <unordered_set>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace mn::clang_engine
{
using namespace mn::language;
inline std::filesystem::path utf8_path(std::string_view source)
{
    std::u8string value;
    value.reserve(source.size());
    for (unsigned char ch : source) value.push_back(static_cast<char8_t>(ch));
    return std::filesystem::path(value);
}
inline bool same_file(std::string_view left, std::string_view right)
{
    if (left.empty() || right.empty()) return false;
    const auto a = utf8_path(left).lexically_normal();
    const auto b = utf8_path(right).lexically_normal();
#ifdef _WIN32
    return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
#else
    return a == b;
#endif
}

using Index = void*;
using TU = void*;
using File = void*;
struct String { const void* data; unsigned flags; };
struct Location { const void* data[2]; unsigned value; };
struct Range { const void* data[2]; unsigned begin; unsigned end; };
struct Cursor { unsigned kind; int xdata; const void* data[3]; };
struct Type { unsigned kind; void* data[2]; };
struct Unsaved { const char* filename; const char* contents; unsigned long length; };
struct Completion { unsigned kind; void* text; };
struct Completions { Completion* results; unsigned count; };
struct Visitor { void* context; unsigned (*visit)(void*, Cursor, Range); };

class Library
{
#ifdef _WIN32
    HMODULE _module = nullptr;
#else
    void* _module = nullptr;
#endif
public:
    explicit Library(const char* path)
    {
#ifdef _WIN32
        const std::filesystem::path name = (path && *path) ? utf8_path(path) : std::filesystem::path(L"libclang.dll");
        // Do not load a DLL from the document directory or the process current directory.
        const auto flags = LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | (name.is_absolute() ? LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR : 0);
        _module = LoadLibraryExW(name.c_str(), nullptr, flags);
#else
        _module = dlopen(path && *path ? path : "libclang.so", RTLD_NOW | RTLD_LOCAL);
#endif
        if (!_module) throw std::runtime_error("libclang could not be loaded. Supply a matching-architecture libclang library via CppLanguageOptions.LibClangPath or MONACONATIVE_LIBCLANG; no clangd process is used.");
    }
    ~Library()
    {
#ifdef _WIN32
        if (_module) FreeLibrary(_module);
#else
        if (_module) dlclose(_module);
#endif
    }
    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
    template<class T> T get(const char* symbol)
    {
#ifdef _WIN32
        const auto ptr = GetProcAddress(_module, symbol);
#else
        const auto ptr = dlsym(_module, symbol);
#endif
        if (!ptr) throw std::runtime_error(std::string("libclang is missing required symbol: ") + symbol);
        return reinterpret_cast<T>(ptr);
    }
};
#define API(result, name, ...) using name##_type = result (*)(__VA_ARGS__); name##_type name = library.get<name##_type>(#name)
struct Api
{
    Library library;
    explicit Api(const char* path) : library(path) {}
    API(Index, clang_createIndex, int, int);
    API(void, clang_disposeIndex, Index);
    API(int, clang_parseTranslationUnit2, Index, const char*, const char* const*, int, Unsaved*, unsigned, unsigned, TU*);
    API(int, clang_reparseTranslationUnit, TU, unsigned, Unsaved*, unsigned);
    API(unsigned, clang_defaultReparseOptions, TU);
    API(void, clang_disposeTranslationUnit, TU);
    API(Completions*, clang_codeCompleteAt, TU, const char*, unsigned, unsigned, Unsaved*, unsigned, unsigned);
    API(void, clang_disposeCodeCompleteResults, Completions*);
    API(unsigned, clang_getNumCompletionChunks, void*);
    API(unsigned, clang_getCompletionChunkKind, void*, unsigned);
    API(String, clang_getCompletionChunkText, void*, unsigned);
    API(unsigned, clang_getCompletionPriority, void*);
    API(unsigned, clang_getCompletionAvailability, void*);
    API(String, clang_getCompletionBriefComment, void*);
    API(const char*, clang_getCString, String);
    API(void, clang_disposeString, String);
    API(String, clang_getClangVersion);
    API(unsigned, clang_getNumDiagnostics, TU);
    API(void*, clang_getDiagnostic, TU, unsigned);
    API(unsigned, clang_getDiagnosticSeverity, void*);
    API(String, clang_getDiagnosticSpelling, void*);
    API(Location, clang_getDiagnosticLocation, void*);
    API(unsigned, clang_getDiagnosticNumRanges, void*);
    API(Range, clang_getDiagnosticRange, void*, unsigned);
    API(void, clang_disposeDiagnostic, void*);
    API(void, clang_getSpellingLocation, Location, File*, unsigned*, unsigned*, unsigned*);
    API(String, clang_getFileName, File);
    API(File, clang_getFile, TU, const char*);
    API(Location, clang_getLocationForOffset, TU, File, unsigned);
    API(Location, clang_getRangeStart, Range);
    API(Location, clang_getRangeEnd, Range);
    API(Cursor, clang_getCursor, TU, Location);
    API(Cursor, clang_getCursorReferenced, Cursor);
    API(Cursor, clang_getCursorDefinition, Cursor);
    API(unsigned, clang_Cursor_isNull, Cursor);
    API(String, clang_getCursorSpelling, Cursor);
    API(String, clang_getCursorDisplayName, Cursor);
    API(String, clang_Cursor_getBriefCommentText, Cursor);
    API(Type, clang_getCursorType, Cursor);
    API(String, clang_getTypeSpelling, Type);
    API(Location, clang_getCursorLocation, Cursor);
    API(Range, clang_getCursorExtent, Cursor);
    API(unsigned, clang_findReferencesInFile, Cursor, File, Visitor);
    API(int, clang_getNumArgTypes, Type);
    API(Type, clang_getArgType, Type, unsigned);
    API(unsigned, clang_isFunctionTypeVariadic, Type);
    API(int, clang_Cursor_getNumArguments, Cursor);
    API(Cursor, clang_Cursor_getArgument, Cursor, unsigned);
    API(Type, clang_getCursorResultType, Cursor);
    API(unsigned, clang_getNumOverloadedDecls, Cursor);
    API(Cursor, clang_getOverloadedDecl, Cursor, unsigned);

    std::string take(String value) const
    {
        const char* data = clang_getCString(value);
        std::string result;
        try { if (data) result = data; }
        catch (...) { clang_disposeString(value); throw; }
        clang_disposeString(value);
        return result;
    }
};
#undef API

struct SourcePoint { std::string file; unsigned offset = 0, line = 0, column = 0; };
class Session
{
    Api api;
    Index index = nullptr;
    TU unit = nullptr;
    std::string parsed_file, parsed_arguments, parsed_contents;
    std::mutex gate;
    SourcePoint point(Location location)
    {
        File file = nullptr; unsigned offset = 0, line = 0, column = 0;
        api.clang_getSpellingLocation(location, &file, &line, &column, &offset);
        return {file ? api.take(api.clang_getFileName(file)) : "", offset, line, column};
    }
    static std::string key(const mn_language_document* docs, uint32_t count)
    {
        std::string value;
        for (uint32_t i = 0; i < count; ++i)
        { value += docs[i].path; value += '\0'; value.append(docs[i].utf8 ? docs[i].utf8 : "", docs[i].byte_length); value += '\0'; }
        return value;
    }
    void prepare(const mn_language_document* docs, uint32_t count, const char* const* args, uint32_t argc, std::vector<Unsaved>& unsaved)
    {
        std::string argumentKey;
        for (uint32_t i = 0; i < argc; ++i) { if (!args[i]) throw std::invalid_argument("A compiler argument is null."); argumentKey += args[i]; argumentKey += '\0'; }
        unsaved.reserve(count);
        for (uint32_t i = 0; i < count; ++i)
        {
            if (docs[i].byte_length > std::numeric_limits<unsigned long>::max()) throw std::length_error("Source exceeds libclang unsigned-long limit.");
            unsaved.push_back({docs[i].path, docs[i].utf8 ? docs[i].utf8 : "", static_cast<unsigned long>(docs[i].byte_length)});
        }
        const auto contentKey = key(docs, count);
        if (unit && (parsed_file != docs[0].path || parsed_arguments != argumentKey))
        { api.clang_disposeTranslationUnit(unit); unit = nullptr; }
        if (unit && parsed_contents != contentKey)
        {
            if (api.clang_reparseTranslationUnit(unit, count, unsaved.data(), api.clang_defaultReparseOptions(unit)) != 0)
            { api.clang_disposeTranslationUnit(unit); unit = nullptr; }
        }
        if (!unit)
        {
            // DetailedPreprocessingRecord | PrecompiledPreamble | CacheCompletionResults |
            // IncludeBriefCommentsInCodeCompletion | KeepGoing. Do NOT skip function bodies.
            constexpr unsigned options = 1U | 4U | 8U | 128U | 512U;
            const int code = api.clang_parseTranslationUnit2(index, docs[0].path, args, static_cast<int>(argc), unsaved.data(), count, options, &unit);
            if (code != 0 || !unit) throw std::runtime_error("libclang could not create a translation unit (error " + std::to_string(code) + "). Check compiler flags, include paths and target toolchain.");
        }
        parsed_file = docs[0].path; parsed_arguments = std::move(argumentKey); parsed_contents = contentKey;
    }
    static const char* kind(unsigned value)
    {
        switch (value)
        {
        case 2: case 3: return "Struct";
        case 4: case 31: case 32: return "Class";
        case 5: return "Enum";
        case 6: return "Field";
        case 7: return "EnumMember";
        case 8: case 30: return "Function";
        case 9: case 10: case 28: return "Variable";
        case 20: case 36: return "Class";
        case 21: case 25: case 26: return "Method";
        case 22: case 33: return "Module";
        case 24: return "Constructor";
        case 27: case 29: return "TypeParameter";
        case 501: case 502: return "Constant";
        case 72: return "Keyword";
        default: return "Text";
        }
    }
    std::string complete(const mn_language_document& doc, size_t offset, std::vector<Unsaved>& unsaved)
    {
        const std::string_view text(doc.utf8 ? doc.utf8 : "", doc.byte_length);
        const auto [start, end] = word_range(text, offset);
        unsigned line = 1, column = 1;
        for (size_t i = 0; i < start; ++i)
        {
            if (text[i] == '\n') { ++line; column = 1; }
            else if (text[i] == '\r' && (i + 1 == start || text[i + 1] != '\n')) { ++line; column = 1; }
            else ++column;
        }
        Completions* raw = api.clang_codeCompleteAt(unit, doc.path, line, column, unsaved.data(), static_cast<unsigned>(unsaved.size()), 1U | 2U | 4U);
        if (!raw) return "{\"items\":[]}";
        const auto deleter = [this](Completions* value) { api.clang_disposeCodeCompleteResults(value); };
        const std::unique_ptr<Completions, decltype(deleter)> owner(raw, deleter);
        std::string items = "[";
        std::unordered_set<std::string> seen;
        for (unsigned i = 0; i < raw->count; ++i)
        {
            auto& candidate = raw->results[i];
            const unsigned availability = api.clang_getCompletionAvailability(candidate.text);
            if (availability >= 2) continue; // NotAvailable / NotAccessible.
            std::string label, detail, insertion, resultType;
            unsigned tab = 0;
            const auto count = api.clang_getNumCompletionChunks(candidate.text);
            for (unsigned j = 0; j < count; ++j)
            {
                const unsigned chunk = api.clang_getCompletionChunkKind(candidate.text, j);
                if (chunk == 0) continue; // Optional argument group: keep the minimal valid call.
                const auto value = api.take(api.clang_getCompletionChunkText(candidate.text, j));
                if (chunk == 15) { resultType = value; continue; }
                detail += value;
                if (chunk == 1) label += value;
                if (chunk == 4) continue; // Informative chunks are display-only.
                if (chunk == 3) insertion += "${" + std::to_string(++tab) + ":" + snippet_escape(value) + "}";
                else if (chunk == 5) insertion += "$0"; // CurrentParameter.
                else insertion += snippet_escape(value);
            }
            if (label.empty() || !seen.insert(label + '\n' + detail).second) continue;
            if (tab > 0 || candidate.kind == 8 || candidate.kind == 21 || candidate.kind == 24 || candidate.kind == 30) insertion += "$0";
            std::ostringstream sort; sort << std::setw(6) << std::setfill('0') << api.clang_getCompletionPriority(candidate.text) << ':' << label;
            append(items, "{\"label\":" + quote(label) + ",\"kind\":" + quote(kind(candidate.kind)) +
                ",\"detail\":" + quote(resultType.empty() ? detail : resultType + " " + detail) +
                ",\"documentation\":" + quote(api.take(api.clang_getCompletionBriefComment(candidate.text))) +
                ",\"snippet\":" + quote(insertion) + ",\"sort\":" + quote(sort.str()) +
                ",\"deprecated\":" + (availability == 1 ? "true" : "false") + ',' + range_json(start, end) + "}");
        }
        return "{\"items\":" + items + "]}";
    }
    std::string diagnostics(const mn_language_document& doc)
    {
        std::string items = "[";
        for (unsigned i = 0, count = api.clang_getNumDiagnostics(unit); i < count; ++i)
        {
            void* diagnostic = api.clang_getDiagnostic(unit, i);
            const auto deleter = [this](void* value) { api.clang_disposeDiagnostic(value); };
            const std::unique_ptr<void, decltype(deleter)> owner(diagnostic, deleter);
            const auto location = point(api.clang_getDiagnosticLocation(diagnostic));
            const auto severity = api.clang_getDiagnosticSeverity(diagnostic);
            if (severity == 0) continue;
            if (!same_file(location.file, doc.path))
            {
                // An include/toolchain error must not turn into an apparently healthy
                // empty completion list. Anchor it at the active document and retain
                // the actual dependency path/location in the diagnostic text.
                if (severity >= 2)
                {
                    const auto prefix = location.file.empty() ? "Compiler configuration: " :
                        "In " + location.file + ":" + std::to_string(location.line) + ":" + std::to_string(location.column) + ": ";
                    append(items, "{" + range_json(0, 0) + ",\"severity\":" + std::to_string(severity >= 3 ? 1 : 2) +
                        ",\"message\":" + quote(prefix + api.take(api.clang_getDiagnosticSpelling(diagnostic))) + "}");
                }
                continue;
            }
            size_t start = location.offset, end = std::min(start + 1, doc.byte_length);
            if (api.clang_getDiagnosticNumRanges(diagnostic))
            {
                const auto range = api.clang_getDiagnosticRange(diagnostic, 0);
                const auto a = point(api.clang_getRangeStart(range)), b = point(api.clang_getRangeEnd(range));
                if (same_file(a.file, doc.path) && same_file(b.file, doc.path)) { start = a.offset; end = b.offset; }
            }
            append(items, "{" + range_json(start, end) + ",\"severity\":" + std::to_string(severity >= 3 ? 1 : severity == 2 ? 2 : 3) +
                ",\"message\":" + quote(api.take(api.clang_getDiagnosticSpelling(diagnostic))) + "}");
        }
        return "{\"diagnostics\":" + items + "]}";
    }
    Cursor cursor_at(const mn_language_document& doc, size_t offset)
    {
        auto file = api.clang_getFile(unit, doc.path);
        if (!file) return {};
        return api.clang_getCursor(unit, api.clang_getLocationForOffset(unit, file, static_cast<unsigned>(std::min(offset, doc.byte_length))));
    }
    std::string hover(const mn_language_document& doc, size_t offset)
    {
        auto cursor = cursor_at(doc, offset);
        if (api.clang_Cursor_isNull(cursor)) return "{}";
        auto reference = api.clang_getCursorReferenced(cursor);
        if (!api.clang_Cursor_isNull(reference)) cursor = reference;
        const auto type = api.take(api.clang_getTypeSpelling(api.clang_getCursorType(cursor)));
        const auto display = api.take(api.clang_getCursorDisplayName(cursor));
        const auto comment = api.take(api.clang_Cursor_getBriefCommentText(cursor));
        const auto [start, end] = word_range(std::string_view(doc.utf8 ? doc.utf8 : "", doc.byte_length), offset);
        return "{\"hover\":" + quote("```cpp\n" + type + (type.empty() ? "" : " ") + display + "\n```" + (comment.empty() ? "" : "\n\n" + comment)) + ',' + range_json(start, end) + "}";
    }
    std::string definition(const mn_language_document& doc, size_t offset)
    {
        auto cursor = cursor_at(doc, offset);
        auto referenced = api.clang_getCursorReferenced(cursor);
        if (!api.clang_Cursor_isNull(referenced)) cursor = referenced;
        if (api.clang_Cursor_isNull(cursor)) return "{\"locations\":[]}";
        // Offer both the definition and the declaration when they differ. This supports
        // header/implementation navigation without a project-wide index or disk crawl.
        const auto definition = api.clang_getCursorDefinition(cursor);
        std::string output = "[";
        std::unordered_set<std::string> added;
        for (auto candidate : {definition, cursor})
        {
            if (api.clang_Cursor_isNull(candidate)) continue;
            const auto location = point(api.clang_getCursorLocation(candidate));
            if (location.file.empty()) continue;
            const auto key = location.file + ':' + std::to_string(location.offset);
            if (!added.insert(key).second) continue;
            append(output, "{\"file\":" + quote(location.file) + ',' +
                range_json(location.offset, location.offset + api.take(api.clang_getCursorSpelling(candidate)).size()) + "}");
        }
        return "{\"locations\":" + output + "]}";
    }
    struct CallSite
    {
        size_t open = 0;
        unsigned argument = 0;
        bool valid = false;
    };
    static CallSite call_site(std::string_view source, size_t offset)
    {
        struct Frame { char delimiter; size_t open; unsigned commas; };
        std::vector<Frame> stack;
        enum class Mode { Code, String, Character, LineComment, BlockComment } mode = Mode::Code;
        bool escape = false;
        for (size_t i = 0; i < std::min(offset, source.size()); ++i)
        {
            const auto c = source[i], next = i + 1 < source.size() ? source[i + 1] : '\0';
            if (mode == Mode::LineComment) { if (c == '\n') mode = Mode::Code; continue; }
            if (mode == Mode::BlockComment)
            {
                if (c == '*' && next == '/') { mode = Mode::Code; ++i; }
                continue;
            }
            if (mode == Mode::String || mode == Mode::Character)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == (mode == Mode::String ? '"' : '\'')) mode = Mode::Code;
                continue;
            }
            if (c == '/' && next == '/') { mode = Mode::LineComment; ++i; continue; }
            if (c == '/' && next == '*') { mode = Mode::BlockComment; ++i; continue; }
            if (c == '"') { mode = Mode::String; continue; }
            if (c == '\'') { mode = Mode::Character; continue; }
            if (c == '(' || c == '[' || c == '{') stack.push_back({c, i, 0});
            else if (c == ')' || c == ']' || c == '}')
            {
                const auto matches = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (!stack.empty() && stack.back().delimiter == matches) stack.pop_back();
            }
            else if (c == ',' && !stack.empty() && stack.back().delimiter == '(') ++stack.back().commas;
        }
        for (auto it = stack.rbegin(); it != stack.rend(); ++it)
            if (it->delimiter == '(') return {it->open, it->commas, true};
        return {};
    }
    std::string signature(const mn_language_document& doc, size_t offset)
    {
        const std::string_view source(doc.utf8 ? doc.utf8 : "", doc.byte_length);
        const auto call = call_site(source, offset);
        if (!call.valid || call.open == 0) return "{\"signatures\":[]}";
        size_t token = call.open;
        while (token && (source[token - 1] == ' ' || source[token - 1] == '\t')) --token;
        if (!token) return "{\"signatures\":[]}";
        --token;
        auto cursor = cursor_at(doc, token);
        if (api.clang_Cursor_isNull(cursor)) return "{\"signatures\":[]}";
        auto referenced = api.clang_getCursorReferenced(cursor);
        if (!api.clang_Cursor_isNull(referenced)) cursor = referenced;
        std::vector<Cursor> candidates;
        auto count = api.clang_getNumOverloadedDecls(cursor);
        if (count > 128) count = 128;
        for (unsigned i = 0; i < count; ++i) candidates.push_back(api.clang_getOverloadedDecl(cursor, i));
        if (candidates.empty()) candidates.push_back(cursor);
        std::string list = "[";
        std::unordered_set<std::string> seen;
        int chosen = 0, best = -100000, current = 0;
        for (auto candidate : candidates)
        {
            if (api.clang_Cursor_isNull(candidate)) continue;
            const auto type = api.clang_getCursorType(candidate);
            const int arguments = api.clang_getNumArgTypes(type);
            if (arguments < 0 || arguments > 128) continue;
            const auto name = api.take(api.clang_getCursorSpelling(candidate));
            if (name.empty()) continue;
            std::string parameters = "[", label = name + "(";
            for (int i = 0; i < arguments; ++i)
            {
                const auto spelling = api.take(api.clang_getTypeSpelling(api.clang_getArgType(type, static_cast<unsigned>(i))));
                const auto arg = api.clang_Cursor_getArgument(candidate, static_cast<unsigned>(i));
                const auto argName = api.clang_Cursor_isNull(arg) ? std::string{} : api.take(api.clang_getCursorSpelling(arg));
                const auto parameter = spelling + (argName.empty() ? "" : " " + argName);
                if (i) label += ", ";
                label += parameter;
                append(parameters, "{\"label\":" + quote(parameter) + "}");
            }
            const bool variadic = api.clang_isFunctionTypeVariadic(type) != 0;
            if (variadic) label += arguments ? ", ..." : "...";
            label += ')';
            const auto returnType = api.take(api.clang_getTypeSpelling(api.clang_getCursorResultType(candidate)));
            if (!returnType.empty() && returnType != "<invalid>") label = returnType + " " + label;
            if (!seen.insert(label).second) continue;
            const int score = call.argument < static_cast<unsigned>(arguments) ? 100 - (arguments - static_cast<int>(call.argument)) * 2 :
                variadic ? 80 : 0 - (static_cast<int>(call.argument) - arguments + 1) * 10;
            if (score > best) { best = score; chosen = current; }
            append(list, "{\"label\":" + quote(label) + ",\"parameters\":" + parameters +
                "],\"activeParameter\":" + std::to_string(call.argument) + "}");
            ++current;
        }
        return "{\"signatures\":" + list + "],\"activeSignature\":" + std::to_string(chosen) +
            ",\"activeParameter\":" + std::to_string(call.argument) + "}";
    }
    struct ReferencesContext { Session* owner; std::string output = "["; bool failed = false; };
    static unsigned reference_visitor(void* context, Cursor, Range range) noexcept
    {
        auto& state = *static_cast<ReferencesContext*>(context);
        try
        {
            const auto a = state.owner->point(state.owner->api.clang_getRangeStart(range));
            const auto b = state.owner->point(state.owner->api.clang_getRangeEnd(range));
            if (a.file == b.file && !a.file.empty()) append(state.output, "{\"file\":" + quote(a.file) + ',' + range_json(a.offset, b.offset) + "}");
            return 1; // CXVisit_Continue.
        }
        catch (...) { state.failed = true; return 0; }
    }
    std::string references(const mn_language_document& doc, size_t offset)
    {
        auto cursor = cursor_at(doc, offset);
        const auto referenced = api.clang_getCursorReferenced(cursor);
        if (!api.clang_Cursor_isNull(referenced)) cursor = referenced;
        auto file = api.clang_getFile(unit, doc.path);
        if (!file || api.clang_Cursor_isNull(cursor)) return "{\"locations\":[]}";
        ReferencesContext state{this};
        api.clang_findReferencesInFile(cursor, file, Visitor{&state, reference_visitor});
        if (state.failed) throw std::runtime_error("Could not collect references.");
        return "{\"locations\":" + state.output + "]}";
    }
public:
    explicit Session(const char* path) : api(path), index(api.clang_createIndex(0, 0))
    { if (!index) throw std::runtime_error("libclang failed to create an index."); }
    ~Session() { if (unit) api.clang_disposeTranslationUnit(unit); if (index) api.clang_disposeIndex(index); }
    std::string request(const mn_language_document* docs, uint32_t count, const char* const* args, uint32_t argc, uint32_t operation, size_t offset)
    {
        validate(docs, count);
        if (argc > 16384 || (argc && !args)) throw std::invalid_argument("Invalid compiler arguments.");
        if (operation > MN_LANGUAGE_SIGNATURE) throw std::invalid_argument("Unsupported C++ operation.");
        if (offset > docs[0].byte_length) throw std::out_of_range("Offset exceeds source length.");
        std::lock_guard<std::mutex> lock(gate);
        std::vector<Unsaved> unsaved;
        prepare(docs, count, args, argc, unsaved);
        switch (operation)
        {
        case MN_LANGUAGE_COMPLETION: return complete(docs[0], offset, unsaved);
        case MN_LANGUAGE_DIAGNOSTICS: return diagnostics(docs[0]);
        case MN_LANGUAGE_HOVER: return hover(docs[0], offset);
        case MN_LANGUAGE_DEFINITION: return definition(docs[0], offset);
        case MN_LANGUAGE_REFERENCES: return references(docs[0], offset);
        case MN_LANGUAGE_SIGNATURE: return signature(docs[0], offset);
        default: throw std::invalid_argument("Unsupported C++ operation.");
        }
    }
};
}

MN_API void* MN_CALL mn_cpp_create(const char* library_path, char** error)
{
    if (error) *error = nullptr;
    try { return new mn::clang_engine::Session(library_path); }
    catch (const std::exception& ex) { mn::language::fail(error, ex.what()); return nullptr; }
    catch (...) { mn::language::fail(error, "Unknown native C++ initialization failure."); return nullptr; }
}
MN_API void MN_CALL mn_cpp_destroy(void* handle) { delete static_cast<mn::clang_engine::Session*>(handle); }
MN_API int MN_CALL mn_cpp_request(void* handle, const mn_language_document* documents,
    uint32_t document_count, const char* const* arguments, uint32_t argument_count,
    uint32_t operation, size_t byte_offset, char** result)
{
    if (result) *result = nullptr;
    try
    {
        if (!handle || !result) throw std::invalid_argument("A live C++ session and result pointer are required.");
        return mn::language::output(result, static_cast<mn::clang_engine::Session*>(handle)->request(documents, document_count, arguments, argument_count, operation, byte_offset));
    }
    catch (const std::exception& ex) { return mn::language::fail(result, ex.what()); }
    catch (...) { return mn::language::fail(result, "Unknown native C++ request failure."); }
}
