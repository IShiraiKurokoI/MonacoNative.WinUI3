#include "MonacoNative.Language.Native.h"
#include <cstdlib>
MN_API uint32_t MN_CALL mn_language_abi_version() { return 1; }
MN_API void MN_CALL mn_language_free(char* value) { std::free(value); }

static_assert(sizeof(mn_language_document) == sizeof(void*) * 3, "Language document ABI layout drift.");
static_assert(offsetof(mn_language_document, path) == 0);
static_assert(offsetof(mn_language_document, utf8) == sizeof(void*));
static_assert(offsetof(mn_language_document, byte_length) == sizeof(void*) * 2);
