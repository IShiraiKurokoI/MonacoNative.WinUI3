# Technical notes

## Architecture and compatibility contract

The frozen reference is Monaco Editor `0.57.0` / upstream VS Code editor-core commit `6a598d4a13031703d483d103c1d934a36ad27971`. Preserve its core algorithms, observables, edit ordering, Unicode/UTF-16 semantics, undo boundaries, view mapping, and complexity characteristics. Platform-specific WinUI 3 native rendering and TSF composition replace only browser-dependent infrastructure.

The portable C++20 engine (`MonacoNative.Core`) owns the piece-tree text model and view projection. The stable C ABI (`MonacoNative.Core.Native`) feeds `.NET 8` wrappers (`MonacoNative.Core.Managed`) and the `MonacoNative.Renderer.WinUI3` user control. Win2D `CanvasSwapChainPanel` composites separately invalidated layers; DirectWrite text layout caches and partial minimap caches bound hot paths to visible regions. The native TSF bridge provides Windows IME composition and ACP geometry; the old TextBox proxy is absent. `MonacoNative.WinRT.Component` offers the C++/WinRT host.

## Mouse pointers

`EditorPointerCursorPolicy` separates geometry-dependent cursor state from WinUI: `Text` (I-beam over editable/viewable text and trailing whitespace), `Arrow` (gutter, overview ruler, outside/overlays), `Minimap` (hand over navigable minimap). The WinUI adapter uses native `InputSystemCursor.Create` and the editor's `ProtectedCursor`, caching cursor objects and only setting the property on transitions. Pointer enter/move/exit, capture/release and child overlays are covered. The ScrollViewer may mark pointer moves handled, so the editor subscribes with `handledEventsToo` and only changes selection during drag; normal hover never forces a renderer frame. Scrollbar thumbs and flyouts retain framework hit testing. This is not a promise that every browser CSS cursor subtype in Monaco has a native equivalent; extend the policy when more editor interaction zones exist.

## Opt-in method profiler

`EditorMethodProfiler` measures synchronous instrumented methods on the WinUI **UI thread**. The fast path is disabled by default: `Measure` returns null without allocating scope objects or sampling timestamps. Enabled scopes record `Stopwatch.GetTimestamp()` wall time and `GC.GetAllocatedBytesForCurrentThread()` before/after. Parent/child scope relationships give *inclusive* and *exclusive* time and managed allocation counts. Methods are aggregated by name with call count, total, mean, P95 (last 1024 samples), maximum, and accumulated managed allocations. Snapshot sorting uses exclusive time. Instrumented areas cover rendering, layout, hit-testing, input, selection, language drawing, and contributions.

**Interpretation:** inclusive wall time is not per-method CPU consumption; the profiler cannot attribute native Win2D/DirectWrite GPU allocations, full process working set, retained heap, other threads or the internals of the native C++ core. The parent's self time includes noninstrumented child methods. Measure actual CPU/native stacks with Windows Performance Recorder / WPA or Visual Studio Profiler when needed. The UI automation report additionally samples the sample process peak working set and CPU time at the process level. Activating the profiler modifies timing and allocation behavior; benchmark both with and without it. Async continuations are not timed with a scope spanning `await`.

## Input and testing invariants

Tests exercise pure cursor classification and profiler aggregation on all available .NET 8 hosts. The interactive Windows suite launches the real sample, drives UIA and OS mouse/keyboard input, verifies document text, selection and pointer states, and checks JSON/CSV artifacts. CI/native gates remain independent of UI automation because a headless runner cannot perform meaningful pointer/IME tests. The script requires an unlocked desktop, foreground access and matching display scaling.

## Other constraints

- Preserve the sole [native ABI 1.2.0 contract](NATIVE_ABI.md), including all 88 exports and layout checks, and Monaco-derived grapheme-safe navigation and performance work-count ceilings.
- Use generated XAML/Win2D resources only from the UI thread; tear down subscriptions and cursors on disposal.
- No WebView2, hidden TextBox proxy, TS/JS scripting runtime, or browser editor fallback.
- Package scripts maintain Windows SDK 26100 compile targeting with App SDK 2.4 pinned independently.

## UI responsiveness, folding, theme, and context menu

- **Selection/async debounce:** Pointer drag updates the caret/selection immediately, but does not schedule expensive contribution-provider tasks for every move. A single UI-thread `DispatcherTimer` coalesces the pending work, with provider refresh following pointer release. LSP text-change debounce also uses a reusable timer. This eliminates the `Task.Delay(..., cancellationToken)` first-chance `TaskCanceledException` storm from repeatedly canceled selection delays; genuine provider cancellation remains supported. Drag selection does not broadcast redundant TSF layout changes until pointer release. A first-chance cancellation from another subsystem is still possible and must be confirmed by a stack trace before attributing remaining latency.
- **Theme:** palette selection prefers the editor's explicitly assigned `RequestedTheme` instead of assuming `ActualTheme` has propagated in the same tick. The SampleApp calls `RefreshEditorTheme()` synchronously after changing its theme. All renderer layers and token-color caches are invalidated as one palette transaction.
- **Folding:** fold chevrons are drawn beside line numbers and click through `ToggleFold` into the native `MonacoViewModel.SetHiddenAreas` projection. A debounced linear-time fallback provider handles curly/square brackets, indentation and `#region` markers. `ShowFolding`, `CollapseAllFolds`, `ExpandAllFolds`, `ToggleFold(lineNumber)`, `AvailableFoldCount`, `CollapsedFoldCount`, and `FoldingChanged` are public. The fallback is not a substitute for full language-server folding ranges or language-aware parsing of comments and strings across lines. It is isolated from rendering/pointer hot paths.
- **Context menu:** individual built-in commands run through the same `EditorCommandService` as keybindings, but use a temporary editor-focused context while the flyout has focus. View/capability switches are native `ToggleMenuFlyoutItem`s with `IsChecked` backed by the corresponding editor property. Cut/copy/paste/etc. continue to respect read-only and selection conditions.

The SampleApp declares per-command menu options in **XAML** using a collection of `EditorContextMenuItemOptions` dependency objects. Standard `Visibility="Collapsed"` hides an item; `IsEnabled="False"` disables the menu item independently of its keyboard shortcut; both default to Visible/True if no override exists. For example:

```xml
<renderer:MonacoEditor.ContextMenuItemOptions>
    <renderer:EditorContextMenuItemOptions CommandId="editor.action.clipboardPasteAction"
                                           Visibility="Collapsed" IsEnabled="True" />
    <renderer:EditorContextMenuItemOptions CommandId="monacoNative.toggleCodeLens"
                                           Visibility="Visible" IsEnabled="False" />
</renderer:MonacoEditor.ContextMenuItemOptions>
```

Menu-only overrides don't disable a feature or keyboard binding. To actually disable an editor contribution use the corresponding editor property (e.g. `CodeLensEnabled="False"`), and to forbid mutations set `IsReadOnly="True"`. Tests verify native command/selection semantics on Windows separately from core C++ tests.

## Sample assets, languages, and editor Find

- Asset source-of-truth: `src/MonacoNative.SampleApp/Assets/Samples`. The application reads each example with `File.ReadAllText` and sets `MonacoEditor.LanguageId`; the `.csproj` includes `CopyToOutputDirectory=PreserveNewest` for the assets.
- Lexical highlighting is a registered Monarch-style provider for `cpp`, `javascript`, `csharp`, and `html`; language-aware completion, diagnostics and semantic tokens require an explicitly attached external `LspClient` via `AttachLanguageServerAsync`. The sample reports LSP disconnected by default. HTML embedded `<script>` and `<style>` are currently handled as HTML text, not embedded JS/CSS language switching.
- Common literal Find/Replace uses the native model (`FindMatches`, `ReplaceAll`). Regex mode lives in the standalone `EditorFindEngine` with a 150-ms regex timeout, 10,000-result cap, and UTF-16 offset conversion. Regex replacement supports .NET capture substitutions (`$1`, `${name}`); automatic preserve-case detects upper/lower/leading-title case. Custom Replace All operations are applied from end to beginning inside `MonacoUndoTransaction` and must call `Complete()` before disposal (otherwise the transaction rolls back). Zero-width matches are skipped.
- Find result decorations are part of the existing selection layer, using a binary search to paint at most 350 matches intersecting the current viewport. They are not recomputed on every mouse move. The selection-restricted range is captured when the option is enabled and reset after Replace All.
- The left panel operates on real editor options, not duplicate render settings. Menu-only Show/Enable overrides use `EditorContextMenuItemOptions` dependency objects so XAML consumers retain full control; `EditorViewOptionsChanged` synchronizes SampleApp toggles after context-menu execution.

## Editor command availability (XAML)

Non-basic non-toggle actions are hidden from the default right-click menu, but retain their registered shortcuts. Set a command's mode in XAML to expose its menu entry or to prohibit its registered shortcut:

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

`Mode` is a dependency property, so host code can update it at runtime. `Disabled` is checked by the command service on both direct command dispatch and resolved keybinding execution; it does not disable unrelated programmatic document editing APIs. Standard `Visibility` and `IsEnabled` remain backward-compatible **menu-only** customization when `Mode=Default`; the explicit `Mode` takes precedence for access control. Fundamental text-input commands have no context-menu entry unless explicitly configured.

The toolbar **View Options** uses `ToggleMenuFlyoutItem` controls synchronized with the actual view properties, and the sidebar edits advanced command policy only.

## Sample UI, localization, find, and minimap

- The toolbar's native WinUI `DropDownButton` draws its own disclosure chevron. Its translated label and the ten translated `ToggleMenuFlyoutItem`s use the existing `MonacoLocalizationService` data in `Locales/*.json`. The sample sidebar and find/replace widget use those same culture changes at runtime, including names and tooltips. CI checks the keys across English, Simplified Chinese, Japanese, and Korean.
- The in-editor find widget is a single compact row with matching options, navigation, selection restriction, and match count; the second row contains replacement controls **only when expanded**. Its placement reserves the full minimap width and overview ruler, recomputed on editor resize or option changes. The expand chevron is a `FontIcon` whose glyph changes with the actual replace-row state.
- The minimap no longer renders full-height bars representing line lengths. It draws compact text-like token spans preserving indentation, whitespace gaps, punctuation, and basic quote/comment cues at about 2.5 device-independent pixels per projected line. The preview cache is lazy and bounded to 700 rows; full-document summaries remain incremental, and scrolling doesn't parse the whole document. This is an approximate visual overview, not actual tiny DirectWrite glyph rendering or embedded-language tokenization.
- The SampleApp registers a conservative local `IDocumentFormattingEditProvider` for each asset language. Switching a sample performs an automatic format; the toolbar **Format** action does the same. This normalizes indentation/trailing whitespace and handles basic HTML tags and brace nesting while preserving quoted code content. It is **not** a complete Prettier/clang-format/Roslyn formatter. Production semantic/structural formatting still requires appropriate language providers or a connected LSP server.
- Right-click view toggles are hidden by default; the toolbar toggles and registered shortcuts remain usable. To opt an individual toggle into the right-click menu declaratively, add an `EditorContextMenuItemOptions` entry with its command ID and `Mode="ContextMenuAndShortcut"`, or call `editor.SetContextMenuToggleVisibility(commandId, true)` from host code. `Mode="ShortcutOnly"` hides the right-click entry while retaining keyboard dispatch; `Mode="Disabled"` also rejects registered shortcuts. Menu placement never controls the corresponding view property itself.
