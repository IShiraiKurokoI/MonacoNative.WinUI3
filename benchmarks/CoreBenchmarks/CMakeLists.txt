find_package(Threads REQUIRED)

add_executable(MonacoNative.Core.Benchmarks main.cpp)
target_link_libraries(MonacoNative.Core.Benchmarks PRIVATE MonacoNative.Core Threads::Threads)
target_include_directories(MonacoNative.Core.Benchmarks PRIVATE
    ${PROJECT_SOURCE_DIR}/src/MonacoNative.Core/include
)
target_compile_features(MonacoNative.Core.Benchmarks PRIVATE cxx_std_20)
if(MSVC)
    target_compile_options(MonacoNative.Core.Benchmarks PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.Core.Benchmarks PRIVATE -Wall -Wextra -Wpedantic)
endif()
