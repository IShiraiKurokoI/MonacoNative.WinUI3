# Design choices and architecture

[Home](../README.md) · [Language services](LANGUAGE_SERVICES.md) · [Building](BUILDING.md) · [Testing](TESTING.md) · [Upstream and licenses](UPSTREAM.md)

## Why I am building this

MonacoNative.WinUI3 is a personal WinUI 3 editor project. The editor is implemented as a native desktop control without WebView2. Monaco Editor and VS Code provide references for editor behavior and selected algorithms; the VS Code workbench, extension host, and browser runtime are not included.

I distinguish a familiar editing experience from a complete upstream reimplementation. My immediate goal is reliable everyday editing, followed by incremental language improvements. I document missing features and Windows validation gaps rather than implying that they are complete.

## My technology choices

### A C++20 core and a C# host

Text state, PieceTree storage, edit transactions, undo/redo, cursors, and view projection live in the C++20 core. WinUI accesses the same document through a C ABI and a .NET 8 wrapper.

The C++ core is tested independently from WinUI lifetime code. The current core ABI is **1.4.0**, with **93 contracted exports**; the language extension ABI is separately versioned as 1. When an interface changes, I update declarations, managed mappings, and tests together. I do not rely on mixing DLLs from different builds. See [Native ABI](NATIVE_ABI.md).

### WinUI 3, Win2D, and DirectWrite

The host uses WinUI 3 and the editor surface uses Win2D `CanvasSwapChainPanel`. DirectWrite-backed `CanvasTextLayout` measures text, and measured widths feed back into the native view model for wrapping. This separates text state from drawing while keeping a native Windows control host.

Rendering uses viewport culling, layout caches, layered invalidation, and a bounded preview cache. Work counters and benchmarks cover hot paths.

### Text input through TSF

I do not use a hidden `TextBox` as the editor's backing input store. Native Windows TSF reads and writes the same text model. WinUI supplies focus, selection, and screen geometry; composition uses the existing decoration and undo paths. `CharacterReceived` remains a basic fallback only when TSF has not taken over direct character input.

IME composition, cursor movement, and undo need real desktop tests. Linux core tests cannot substitute for that validation.

### Language services: reuse libraries where appropriate, port the rest

| Area | My approach | What I am not doing |
| --- | --- | --- |
| C# | Official `Microsoft.CodeAnalysis.CSharp.Features [4.11.0]`, in process | Requiring a Roslyn source build or starting Roslyn Language Server |
| C++ | Fixed `libclang.runtime.win-x64` / `win-arm64 [17.0.4]`, through the existing native bridge | Requiring an LLVM build or starting clangd |
| JSON / HTML / CSS | Port explicitly identified modules and tests to C#; label host additions separately | Running npm language services or calling a small rule set a complete upstream engine |
| JavaScript / TypeScript | Port scanning and preprocessing to C++, with basic declaration and editing services | Complex npm package resolution, a full tsconfig project system, or a complete type checker in this phase |

C# and C++ use precompiled in-process libraries. JSON, HTML, CSS, JavaScript, and TypeScript use translated modules in this repository. Source mappings, translated upstream tests, differential fixtures, and project tests are listed in [upstream sources and licenses](UPSTREAM.md).

### Build-time native checks without executing target code

I added read-only PE inspection because a successful managed build does not establish that a dynamically loaded native DLL is deployable. MSBuild checks file presence, target architecture, and required exports in the relevant build and publish outputs. It never loads those DLLs into the build process, so cross-architecture inspection is possible.

Runtime ABI calls, DLL loading, and semantic smoke tests run in the integration-test layer. Static inspection checks deployment structure and exports. The exact checks and limitations are in [building](BUILDING.md); the same inspector source is included in the existing managed test project.

## Projects and call paths

```text
MonacoNative.Core (C++)
    → MonacoNative.Core.Native (C ABI / native language bridges)
    → MonacoNative.Core.Managed + MonacoNative.Language + language packages
    → MonacoNative.Renderer.WinUI3
    → MonacoNative.SampleApp
```

| Existing project | Responsibility |
| --- | --- |
| `MonacoNative.Core` | PieceTree, editing, view projection, cursors, folding, undo, search, and diff |
| `MonacoNative.Core.Native` / `Core.Managed` | C ABI, ownership, layout checks, and .NET adaptation |
| `MonacoNative.Renderer.WinUI3` | Drawing, input routing, selections/cursors, minimap, language UI, accessibility |
| `MonacoNative.Input.Native` | Windows TSF text store, composition, and ACP geometry |
| `MonacoNative.Language` | Provider interfaces, registration, UTF-16 snapshots, completion, snippets, and explicit legacy LSP entry points |
| `MonacoNative.Language.{Json,Html,Css,TypeScript,CSharp,Cpp}` | Language configuration, lexical highlighting, and implemented in-process services |
| `MonacoNative.EditorServices` | Portable input state, accessibility, and localization, retaining public namespaces |
| `MonacoNative.WinRT.Component` | C++/WinRT host entry point |
| `MonacoNative.SampleApp` | Samples, find/replace, view options, command policies, and optional profiling |

The frozen reference in [monaco-0.57.0-baseline.json](../reference/monaco-0.57.0-baseline.json) is Monaco **0.57.0**, with VS Code editor-core reference `6a598d4a13031703d483d103c1d934a36ad27971`. It is a repository compatibility reference, not a claim that every feature is equivalent or that every later port shares that source version.

Core tests cover edit ordering, Unicode / UTF-16 semantics, undo boundaries, view mapping, and complexity constraints. WinUI / TSF replace browser-specific drawing and input infrastructure.

## Document state, transactions, and asynchronous work

`MonacoTextModel` owns document and undo state. `BeginUndoTransaction()` creates an explicit transaction: `Complete()` commits; disposal without completion rolls back. A completion's main and additional edits must be checked for overlap, applied in the correct order, and followed by recomputation of the snippet start.

Language requests use immutable `LanguageModelSnapshot` instances. Diagnostics, folding, completion, formatting, and signature help check URI, language, version, and, where needed, cursor position and request sequence. Canceled or stale results must not modify a new document. `LastNativeLanguageServiceError` and `NativeLanguageServiceFailed` expose local-service failures.

`EditorLifetime` owns editor-scoped background work. Completion, diagnostics, folding, parameter hints, contribution refresh, and LSP refresh register their tasks with the lifetime and link their cancellation sources to the same shutdown token. `DisposeAsync()` stops producers first and gives the registered work one shared 75 ms drain budget before resource disposal. `Dispose()` uses the same stop path without waiting, so hosts that cannot await do not block the UI thread. Language packages, input, rendering, view state, and the text model still release the resources they own.

Dragging immediately updates selection and caret, while a reused `DispatcherTimer` coalesces provider refreshes. Heavier work is scheduled after pointer release. Ordinary hover does not force a full frame. TSF layout notifications also avoid repeated broadcasts for every drag movement. Other subsystems can still throw cancellation exceptions; the exception name alone does not identify the originating path.

## Rendering, folding, and themes

The renderer handles viewport drawing, layout caching, incremental invalidation, selections/cursors, whitespace and indentation guides, bracket cues, overview ruler, minimap, high-contrast palettes, and device-loss recovery. XAML / Win2D resources are used on the UI thread. Disposal unsubscribes events and releases cursors.

The minimap draws compact token silhouettes that preserve indentation, whitespace, and punctuation, at about 2.5 device-independent pixels per projected line. Its preview cache is capped at 700 lines. This is an approximate overview, not scaled-down DirectWrite glyph rendering or complete embedded-language coloring.

Folding asynchronously requests `IFoldingRangeProvider` ranges and projects them through `MonacoViewModel.SetHiddenAreas`. An empty provider result is authoritative; generic folding is used only when no provider applies. Host APIs include `ShowFolding`, `CollapseAllFolds`, `ExpandAllFolds`, `ToggleFold`, `AvailableFoldCount`, `CollapsedFoldCount`, and `FoldingChanged`.

Theme selection honors the editor's `RequestedTheme`. After switching, the sample calls `RefreshEditorTheme()` to invalidate rendering and color caches as one palette update. Fonts, DPI, suggestion placement, and high-contrast appearance still need Windows testing.

## Pointer behavior and commands

`EditorPointerCursorPolicy` separates hit-test geometry from WinUI. Text and trailing line whitespace use an I-beam; gutters, rulers, overlays, and outside regions use an arrow; a navigable minimap uses a hand. WinUI caches `InputSystemCursor` objects and changes `ProtectedCursor` only when necessary. Handled pointer events are observed, while scrollbars and flyouts retain framework hit testing.

`MonacoKeybindingService` exposes native context keys and shortcut resolution. `EditorCommandService` routes shortcuts and context-menu commands through the same `when` conditions. Basic cut/copy/paste actions keep context-menu entries; other non-toggle commands default to `ShortcutOnly`.

```xml
<renderer:MonacoEditor.ContextMenuItemOptions>
    <renderer:EditorContextMenuItemOptions CommandId="editor.action.rename"
        Mode="ContextMenuAndShortcut" />
    <renderer:EditorContextMenuItemOptions CommandId="editor.action.formatDocument"
        Mode="ShortcutOnly" />
    <renderer:EditorContextMenuItemOptions CommandId="editor.action.addCommentLine"
        Mode="Disabled" />
</renderer:MonacoEditor.ContextMenuItemOptions>
```

`Mode` is a dependency property that can change at runtime. `Disabled` blocks menu/shortcut execution through the public dispatcher, not direct host calls into document APIs. In `Mode=Default`, `Visibility` / `IsEnabled` still affect only menus; use `IsReadOnly` to prevent editing. View toggles live in the toolbar's `View Options`, synchronized through `EditorViewOptionsChanged`. `SetContextMenuToggleVisibility` can expose individual toggles in the context menu.

## Samples, find/replace, and localization

Samples live in [Assets/Samples](../src/MonacoNative.SampleApp/Assets/Samples), are read from files rather than embedded strings, and are copied to the output directory. `demo.ts` is a single-file basic example; `demo.types.ts` remains optional snapshot-test material. The sample project keeps `demo.cs` as a project-backed C# example.

C# formatting uses Roslyn and JSON uses the translated JSONC formatter. HTML, CSS/SCSS/LESS, JavaScript/TypeScript, and C++ register document and range formatters from `MonacoNative.Language.BasicSourceFormattingProvider`. The shared formatter normalizes indentation and trailing whitespace while preserving strings, comments, template literals, HTML void elements, and script/style blocks.

`Ctrl+F` and `Ctrl+H` share a compact panel with case-sensitive, whole-word, regex, and selection-scoped search; previous/next navigation; match counts; preserve-case replacement; and replace-current/all. Literal search uses the native model. The independent `EditorFindEngine` handles regex with a 150 ms timeout, a 10,000-match limit, .NET capture substitution, and no zero-width matches. Preserve-case handles all-uppercase, all-lowercase, and initial capitals. Custom replacements run backward inside one committed transaction.

Find decorations draw only a bounded visible set, at most 350 matches, found through binary search. The panel reserves space for the minimap and ruler. Replacement controls appear only when expanded, with matching icon state. Selection search captures its range when enabled and resets it after replace-all.

`MonacoNative.I18n`, inside `EditorServices`, provides UI localization. `Editor.UiLanguage = "zh-CN"` and `Editor.GetLocalizedString(commandId)` use the existing command keys. Toolbar, sidebar, and find panel follow language changes. Existing checks cover en-US, zh-CN, ja-JP, and ko-KR. Input capability is exposed through `IsImeAvailable`, `IsImeComposing`, `ImeCapability`, and `InputLanguageTag`. English documentation does not remove these localized UI resources.

## Diff, accessibility, and profiling

`MonacoDiffEditor` provides a basic native-diff-backed implementation: side-by-side/inline views, synchronized scrolling, individual reverts, hidden unchanged regions, intra-line differences, and moved-block detection. Accessibility includes UIA Text / Text2 / TextEdit, portable ranges, and screen-reader events. Having an interface does not mean every assistive technology has been tested.

`EditorMethodProfiler` is off by default. Disabled `Measure` calls do not allocate timing scopes. Enabled instrumentation records UI-thread synchronous wall time and managed allocations, aggregates call count, total, mean, P95 over the latest 1,024 calls, maximum, and inclusive/exclusive time, and sorts output by exclusive time by default.

`EditorMethodProfiler` records editor method timing and managed allocations; native CPU sampling and GPU memory analysis use external profilers. It does not measure other threads, retained heap, native-core internals, or Win2D allocations, and does not keep scopes across `await`. Native stacks and CPU sampling still require tools such as WPR / WPA or Visual Studio Profiler. Instrumentation has overhead, so I compare against an uninstrumented baseline.

## Scope

The default path has no WebView2, hidden TextBox backing store, Node / tsserver, or language-server process. The generic LSP client is an explicit legacy-host option, not a substitute for missing ports.

Completion, snippets, Codicon, and ranking are implemented but do not promise pixel-level or complete behavioral parity with VS Code. Public semantic-token, code-action, inlay-hint, and rename interfaces do not imply implementations for every built-in language.

JavaScript and TypeScript target basic syntax and everyday editing. Complex package resolution and the full TypeScript project system are outside the current scope.
