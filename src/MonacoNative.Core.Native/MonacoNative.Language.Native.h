#pragma once
#include "MonacoNative.Core.Native.h"

// Language extension ABI 1. The editor/model ABI remains unchanged.
// Inputs are borrowed for the call. Returned UTF-8 JSON/error buffers are owned by
// the caller and must be released with mn_language_free. No process is launched.
struct mn_language_document
{
    const char* path;
    const char* utf8;
    size_t byte_length;
};

enum mn_language_operation : uint32_t
{
    MN_LANGUAGE_COMPLETION = 0,
    MN_LANGUAGE_DIAGNOSTICS = 1,
    MN_LANGUAGE_HOVER = 2,
    MN_LANGUAGE_DEFINITION = 3,
    MN_LANGUAGE_REFERENCES = 4,
    MN_LANGUAGE_TOKENS = 5,
    MN_LANGUAGE_SIGNATURE = 6,
    // Additive operations; ABI-1 layout/ownership remain unchanged. Preprocess
    // results preserve upstream zero-based UTF-16 offsets (not byte ranges).
    MN_LANGUAGE_PREPROCESS = 7,             // imports + directives; TypeScript mode
    MN_LANGUAGE_PREPROCESS_JS = 8,          // also detect CommonJS / AMD imports
    MN_LANGUAGE_REFERENCE_DIRECTIVES = 9,   // directives only; readImportFiles=false
    MN_LANGUAGE_FOLDING = 10,              // inclusive, 1-based editor line ranges
    MN_LANGUAGE_SIGNATURE_MANUAL = 11      // explicit invocation may inspect strings/comments
};

MN_API uint32_t MN_CALL mn_language_abi_version();
MN_API void MN_CALL mn_language_free(char* value);
MN_API void* MN_CALL mn_cpp_create(const char* library_path, char** error);
MN_API void MN_CALL mn_cpp_destroy(void* handle);
MN_API int MN_CALL mn_cpp_request(void* handle, const mn_language_document* documents,
    uint32_t document_count, const char* const* arguments, uint32_t argument_count,
    uint32_t operation, size_t byte_offset, char** result);
MN_API int MN_CALL mn_typescript_request(const mn_language_document* documents,
    uint32_t document_count, uint32_t operation, size_t byte_offset, char** result);
