add_executable(MonacoNative.NativeAbi.Tests main.cpp UpstreamScannerTests.cpp ScannerApiTests.cpp PreProcessTests.cpp BasicEditingTests.cpp)
target_link_libraries(MonacoNative.NativeAbi.Tests PRIVATE MonacoNative.Core.Native)
target_include_directories(MonacoNative.NativeAbi.Tests PRIVATE
    ${PROJECT_SOURCE_DIR}/src/MonacoNative.Core.Native
    ${PROJECT_SOURCE_DIR}/tests/TestSupport
)
target_compile_features(MonacoNative.NativeAbi.Tests PRIVATE cxx_std_20)
if(MSVC)
    target_compile_options(MonacoNative.NativeAbi.Tests PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.NativeAbi.Tests PRIVATE -Wall -Wextra -Wpedantic)
endif()
if(WIN32)
    # Windows has no build-tree RPATH: the loader searches the executable's own
    # directory, not the sibling CMake target directory where the DLL is built.
    # Without this copy, a missing-DLL system dialog can wait forever in a
    # headless runner before main() (even for the ABI version-only smoke test).
    add_custom_command(TARGET MonacoNative.NativeAbi.Tests POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:MonacoNative.Core.Native>"
            "$<TARGET_FILE_DIR:MonacoNative.NativeAbi.Tests>"
        COMMENT "Copy Native Core DLL next to Windows ABI test executable"
        VERBATIM
    )

    # Version is a canary for loader/runtime initialization. If even this
    # contract cannot run, skip the dependent cases instead of timing out all
    # thirteen processes. Never hide the initial failing test.
    foreach(case_id IN ITEMS version lifetime copy editing search word language view view_null undo undo_cursor diff unicode keybinding null native_language typescript_upstream typescript_scanner_api typescript_preprocess typescript_dependencies typescript_basic)
        set(test_name "MonacoNative.NativeAbi.${case_id}.Tests")
        add_test(NAME "${test_name}" COMMAND MonacoNative.NativeAbi.Tests --case "${case_id}")
        if(case_id STREQUAL "version")
            set_tests_properties("${test_name}" PROPERTIES
                LABELS "native;abi;integration" TIMEOUT 15
                FIXTURES_SETUP MonacoNativeAbiLoaded)
        else()
            set_tests_properties("${test_name}" PROPERTIES
                LABELS "native;abi;integration" TIMEOUT 30
                FIXTURES_REQUIRED MonacoNativeAbiLoaded)
        endif()
    endforeach()
else()
    add_test(NAME MonacoNative.NativeAbi.Tests COMMAND MonacoNative.NativeAbi.Tests)
    set_tests_properties(MonacoNative.NativeAbi.Tests PROPERTIES LABELS "native;abi;integration" TIMEOUT 60)
endif()
