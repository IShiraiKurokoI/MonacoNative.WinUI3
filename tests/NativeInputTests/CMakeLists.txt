add_executable(MonacoNative.NativeInput.Backend.Tests
    NativeInputBackendTests.cpp
)
target_link_libraries(MonacoNative.NativeInput.Backend.Tests PRIVATE MonacoNative.Input.Native.Core)
target_include_directories(MonacoNative.NativeInput.Backend.Tests PRIVATE
    ${PROJECT_SOURCE_DIR}/src/MonacoNative.Input.Native/include
    ${PROJECT_SOURCE_DIR}/tests/TestSupport
)
target_compile_features(MonacoNative.NativeInput.Backend.Tests PRIVATE cxx_std_20)
if(MSVC)
    target_compile_options(MonacoNative.NativeInput.Backend.Tests PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.NativeInput.Backend.Tests PRIVATE -Wall -Wextra -Wpedantic)
endif()
add_test(NAME MonacoNative.NativeInput.Backend.Tests COMMAND MonacoNative.NativeInput.Backend.Tests)
set_tests_properties(MonacoNative.NativeInput.Backend.Tests PROPERTIES LABELS "input;native;unit")

if(WIN32 AND TARGET MonacoNative.Input.Native)
    add_executable(MonacoNative.NativeInput.Tsf.Tests
        TsfTextStoreTests.cpp
    )
    target_link_libraries(MonacoNative.NativeInput.Tsf.Tests PRIVATE MonacoNative.Input.Native ole32 uuid)
    target_include_directories(MonacoNative.NativeInput.Tsf.Tests PRIVATE
        ${PROJECT_SOURCE_DIR}/src/MonacoNative.Input.Native/include
        ${PROJECT_SOURCE_DIR}/tests/TestSupport
    )
    target_compile_features(MonacoNative.NativeInput.Tsf.Tests PRIVATE cxx_std_20)
    target_compile_definitions(MonacoNative.NativeInput.Tsf.Tests PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(MonacoNative.NativeInput.Tsf.Tests PRIVATE /W4 /permissive- /EHsc)
    add_test(NAME MonacoNative.NativeInput.Tsf.Tests COMMAND MonacoNative.NativeInput.Tsf.Tests)
    set_tests_properties(MonacoNative.NativeInput.Tsf.Tests PROPERTIES LABELS "input;native;windows;tsf")
endif()
