cmake_minimum_required(VERSION 3.22)
project(MonacoNativeCore LANGUAGES CXX)

add_library(MonacoNative.Core STATIC
    src/PieceTreeTextBuffer.cpp
    src/UndoRedoService.cpp
    src/TextModel.cpp
    src/Decorations.cpp
    src/ViewModel.cpp
    src/FoldingModel.cpp
    src/CursorModel.cpp
    src/Tokenizer.cpp
    src/DiffEngine.cpp
    src/Commands.cpp
    src/UnicodeSegmentation.cpp
)

target_include_directories(MonacoNative.Core PUBLIC include)
target_compile_features(MonacoNative.Core PUBLIC cxx_std_20)
set_target_properties(MonacoNative.Core PROPERTIES POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(MonacoNative.Core PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(MonacoNative.Core PRIVATE -Wall -Wextra -Wpedantic)
endif()
