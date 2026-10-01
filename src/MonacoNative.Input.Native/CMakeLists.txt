add_library(MonacoNative.Input.Native.Core STATIC
    src/MemoryAcpTextStoreBackend.cpp
    src/TextModelAcpTextStoreBackend.cpp
)

target_include_directories(MonacoNative.Input.Native.Core PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)
target_link_libraries(MonacoNative.Input.Native.Core PUBLIC MonacoNative.Core)
target_compile_features(MonacoNative.Input.Native.Core PUBLIC cxx_std_20)
if(MSVC)
    target_compile_options(MonacoNative.Input.Native.Core PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.Input.Native.Core PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(WIN32)
    add_library(MonacoNative.Input.Native STATIC
        src/TsfTextStore.cpp
        src/TsfDocumentSession.cpp
    )
    target_include_directories(MonacoNative.Input.Native PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    )
    target_link_libraries(MonacoNative.Input.Native PUBLIC
        MonacoNative.Input.Native.Core
        ole32
        uuid
    )
    target_compile_features(MonacoNative.Input.Native PUBLIC cxx_std_20)
    target_compile_definitions(MonacoNative.Input.Native PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX __INLINE_ISEQUAL_GUID)
    target_compile_options(MonacoNative.Input.Native PRIVATE /W4 /permissive- /EHsc)
endif()
