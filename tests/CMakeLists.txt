add_subdirectory(CoreTests)
if(TARGET MonacoNative.Core.Native)
    add_subdirectory(NativeAbiTests)
endif()

add_subdirectory(NativeInputTests)
