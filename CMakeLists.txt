cmake_minimum_required(VERSION 3.22)
project(MonacoNative LANGUAGES CXX)

option(MONACONATIVE_BUILD_TESTS "Build MonacoNative automated tests" ON)
option(MONACONATIVE_BUILD_NATIVE_ABI "Build the portable/native C ABI bridge" ON)
option(MONACONATIVE_BUILD_BENCHMARKS "Build MonacoNative performance benchmark executables" OFF)
option(MONACONATIVE_ENABLE_SANITIZERS "Enable AddressSanitizer and UndefinedBehaviorSanitizer" OFF)
option(MONACONATIVE_ENABLE_COVERAGE "Enable compiler coverage instrumentation" OFF)
option(MONACONATIVE_WARNINGS_AS_ERRORS "Treat compiler warnings as errors" OFF)

if(MONACONATIVE_ENABLE_SANITIZERS AND MSVC)
    message(WARNING "MONACONATIVE_ENABLE_SANITIZERS currently configures GCC/Clang ASan+UBSan only.")
endif()

if(MONACONATIVE_ENABLE_SANITIZERS AND NOT MSVC)
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer)
    add_link_options(-fsanitize=address,undefined -fno-omit-frame-pointer)
endif()

if(MONACONATIVE_ENABLE_COVERAGE AND NOT MSVC)
    add_compile_options(--coverage -O0 -g)
    add_link_options(--coverage)
endif()

# Source files are UTF-8. Without /utf-8, MSVC can silently reinterpret CJK
# UTF-16 literals using the Windows runner's legacy system code page.
if(MSVC)
    add_compile_options(/utf-8)
endif()

if(MONACONATIVE_WARNINGS_AS_ERRORS)
    if(MSVC)
        add_compile_options(/WX)
    else()
        add_compile_options(-Werror)
    endif()
endif()

add_subdirectory(src/MonacoNative.Core)
add_subdirectory(src/MonacoNative.Input.Native)
if(MONACONATIVE_BUILD_NATIVE_ABI)
    add_subdirectory(src/MonacoNative.Core.Native)
endif()

if(MONACONATIVE_BUILD_BENCHMARKS)
    add_subdirectory(benchmarks)
endif()

if(MONACONATIVE_BUILD_TESTS)
    include(CTest)
    enable_testing()
    add_subdirectory(tests)
endif()
