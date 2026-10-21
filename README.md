# MonacoNative.WinUI3

Native Monaco-style editing for WinUI 3 without WebView2, HTML, JavaScript or browser embedding. The project follows **Monaco Editor 0.57.0** text/view algorithms and behavior, with native C++20 core, a stable C ABI, C#/.NET 8 wrappers, a Win2D/DirectWrite renderer and native TSF text input.

**Target:** Windows 10/11, x64 and ARM64; Windows App SDK 2.4.0, Win2D 1.4.0, Windows SDK 26100 (compile), .NET 8. Supported minimum OS: Windows 10 build 19041.

## Main components

- `src/MonacoNative.Core` — C++ piece tree, editing, projection, cursor, folding, undo/redo, search and diff.
- `src/MonacoNative.Core.Native`, `src/MonacoNative.Core.Managed` — C ABI and managed adapters. See [Native ABI contract](docs/NATIVE_ABI.md) for the single supported version and release policy.
- `src/MonacoNative.Renderer.WinUI3` — WinUI 3/Win2D renderer, input routing, decorations, minimap, code intelligence UI, accessibility and method profiler.
- `src/MonacoNative.Input.Native` — Windows Text Services Framework (TSF); no TextBox proxy.
- `src/MonacoNative.Language` — lexical/provider language services and LSP integration (public namespaces `MonacoNative.Language` and `MonacoNative.Lsp`).
- `src/MonacoNative.EditorServices` — portable input/IME, accessibility and localization helpers (existing namespaces retained).
- `src/MonacoNative.SampleApp` — interactive four-language HTML/JavaScript/C#/C++ sample browser, top-toolbar view options and left-side advanced command policies, find/replace and profiler control.

## Sample language support and Find / Replace

- The top **Example** selector reads `Assets/Samples/demo.html`, `demo.js`, `demo.cs`, and `demo.cpp` from files copied beside the executable. Example source code is not embedded in the application C#.
- `MonacoNative.Language` now registers basic **lexical Monarch-like highlighting** and language configurations for HTML, JavaScript, C#, and C++ (also JSON/JSONC). Changing the example updates both the language ID and the text; this does **not** install a language server.
- `MonacoNative.Language` also contains the `MonacoNative.Lsp` transport/clients/providers and `MonacoEditor.AttachLanguageServerAsync(...)` connects an initialized external client. **SampleApp does not spawn a server**; the sidebar reports LSP *not connected* unless an external host attaches one. IntelliSense/semantic analysis/diagnostics depend on a compatible running server.
- Find (`Ctrl+F`) and Replace (`Ctrl+H`) share one collapsible editor popup. Case, whole word, regular expression, search-in-selection, previous/next match, match counts, preserve replacement case, replace current/all are supported. All matches are highlighted in the visible viewport. Plain literal searches keep the native search/replace path; regex uses a capped/time-bounded managed implementation and cannot be treated as a complete replacement for all VS Code search semantics.
- View switches (including folding, minimap, word wrap, and CodeLens) are checked items in the toolbar **View Options** dropdown; they remain synchronized with the context menu and real editor view state.
- Advanced non-toggle commands use the XAML `EditorContextMenuItemOptions.Mode` dependency property: `ContextMenuAndShortcut`, `ShortcutOnly`, or `Disabled`. The **default** for advanced commands is `ShortcutOnly`; basic cut/copy/paste/select all/undo/redo are on the context menu. `Disabled` blocks registered keybindings and context menu execution in the common command dispatcher. `Visibility` and `IsEnabled` remain available for legacy *menu-only* customization if `Mode=Default`.

## Build and test

On Windows with Visual Studio C++ / .NET / Windows SDK installed:

```powershell
./scripts/build-windows.ps1 -Configuration Release -Platform x64
./scripts/test-windows.ps1 -Configuration Release
./scripts/test-ui.ps1 -Configuration Release -SkipBuild
```

The UI suite **requires an unlocked interactive Windows desktop**. It does not run unattended on Linux/headless runners. Method profiling is **opt-in** in SampleApp (`Profile: ON` → interact → `Export`), producing JSON and CSV in `%LOCALAPPDATA%\MonacoNative\Profiles` by default. See `docs/TESTING.md`.

Portable core (Linux/macOS): `cmake -S . -B build && cmake --build build && ctest --test-dir build --output-on-failure`.

## Documentation

- [`docs/TECHNICAL_NOTES.md`](docs/TECHNICAL_NOTES.md): architecture, algorithm/performance invariants, pointer handling, profiler limits.
- [`docs/BUILDING.md`](docs/BUILDING.md): build targets and prerequisites.
- [`docs/TESTING.md`](docs/TESTING.md): unit/UI automation and profiling workflow.

Development history and staged change logs are intentionally not shipped. Third-party attributions remain in `THIRD_PARTY_NOTICES.md`.
