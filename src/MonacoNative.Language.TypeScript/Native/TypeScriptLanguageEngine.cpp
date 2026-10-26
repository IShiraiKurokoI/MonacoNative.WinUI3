#include "TypeScriptScanner.h"
#include "TypeScriptModules.h"

namespace mn::typescript
{
using namespace mn::language;
std::string token_request(const mn_language_document& doc)
{
    Text text(std::string_view(doc.utf8 ? doc.utf8 : "", doc.byte_length));
    Scanner scanner(text);
    std::string tokens = "[";
    for (size_t limit = 0; limit < 2000000; ++limit)
    {
        const auto token = scanner.scan();
        append(tokens, "{\"kind\":" + std::to_string(token.kind) + ",\"start\":" + std::to_string(token.start) +
            ",\"end\":" + std::to_string(token.end) + ",\"value\":" + quote(token.value) + ",\"flags\":" + std::to_string(token.flags) + "}");
        if (token.kind == sk::EndOfFileToken) break;
        if (limit == 1999999) throw std::length_error("TypeScript token budget exceeded.");
    }
    std::string diagnostics = "[";
    for (const auto& error : scanner.errors)
        append(diagnostics, "{" + range_json(text.to_byte(error.start), text.to_byte(error.end)) + ",\"message\":" + quote(error.message) + "}");
    return "{\"tokens\":" + tokens + "],\"diagnostics\":" + diagnostics + "]}";
}
}
MN_API int MN_CALL mn_typescript_request(const mn_language_document* documents,
    uint32_t document_count, uint32_t operation, size_t byte_offset, char** result)
{
    if (result) *result = nullptr;
    try
    {
        mn::language::validate(documents, document_count);
        if (!result || byte_offset > documents[0].byte_length) throw std::invalid_argument("Invalid TypeScript request.");
        if (operation == MN_LANGUAGE_TOKENS) return mn::language::output(result, mn::typescript::token_request(documents[0]));
        if (operation == MN_LANGUAGE_PREPROCESS || operation == MN_LANGUAGE_PREPROCESS_JS || operation == MN_LANGUAGE_REFERENCE_DIRECTIVES)
        {
            const mn::language::Text text(std::string_view(documents[0].utf8 ? documents[0].utf8 : "", documents[0].byte_length));
            return mn::language::output(result, mn::typescript::preprocess_json(mn::typescript::pre_process_file(text,
                operation != MN_LANGUAGE_REFERENCE_DIRECTIVES, operation == MN_LANGUAGE_PREPROCESS_JS)));
        }
        if (operation == MN_LANGUAGE_FOLDING || operation == MN_LANGUAGE_DIAGNOSTICS)
        {
            const mn::language::Text text(std::string_view(documents[0].utf8 ? documents[0].utf8 : "", documents[0].byte_length));
            const mn::typescript::Declarations document(text, documents[0].path);
            return mn::language::output(result, operation == MN_LANGUAGE_FOLDING ? document.folding() : document.diagnostics());
        }
        mn::typescript::ModuleGraph project(documents, document_count);
        return mn::language::output(result, project.request(operation, byte_offset));
    }
    catch (const std::exception& ex) { return mn::language::fail(result, ex.what()); }
    catch (...) { return mn::language::fail(result, "Unknown native TypeScript request failure."); }
}
