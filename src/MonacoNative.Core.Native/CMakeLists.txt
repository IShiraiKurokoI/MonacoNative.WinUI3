add_library(MonacoNative.Core.Native SHARED
    MonacoNative.Core.Native.cpp
)

target_include_directories(MonacoNative.Core.Native
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../MonacoNative.Core/include
)
target_link_libraries(MonacoNative.Core.Native PRIVATE MonacoNative.Core)

# The Windows C ABI exposes TSF sessions. Link their implementation target instead
# of duplicating include paths: its PUBLIC usage requirements supply the TSF
# headers, native input backend, and required Windows system libraries.
if(WIN32)
    target_link_libraries(MonacoNative.Core.Native PRIVATE MonacoNative.Input.Native)
endif()
target_compile_features(MonacoNative.Core.Native PRIVATE cxx_std_20)
target_compile_definitions(MonacoNative.Core.Native PRIVATE MONACONATIVE_CORE_NATIVE_EXPORTS)

if(MSVC)
    target_compile_options(MonacoNative.Core.Native PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.Core.Native PRIVATE -Wall -Wextra -Wpedantic)
endif()
