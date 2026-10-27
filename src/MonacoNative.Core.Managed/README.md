# MonacoNative.Core NuGet

This project exposes the native C++ text engine through a stable C ABI and `MonacoTextModel` .NET wrapper.

Current managed API includes:

- text/length/line count/version,
- language id,
- line content,
- UTF-16 position ↔ offset conversion,
- incremental replace,
- EOL get/set,
- literal search with case/whole-word options,
- bracket matching,
- undo/redo state and commands.

The package contains:

```text
lib/net10.0-windows10.0.26100.0/MonacoNative.Core.Managed.dll
runtimes/win-x64/native/MonacoNative.Core.Native.dll
```

Build the native DLL before packing. The pack target fails if the expected x64 DLL is absent.
