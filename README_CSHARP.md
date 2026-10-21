# MonacoNative — C# WinUI 3 host

Open `MonacoNative.sln`, select `Debug | x64`, restore NuGet packages and build.

Active path:

`MonacoNative.Core (C++) -> MonacoNative.Core.Native.dll -> MonacoNative.Core.Managed + MonacoNative.Language (including LSP) + MonacoNative.EditorServices -> MonacoNative.Renderer.WinUI3 -> MonacoNative.SampleApp`

The WinUI renderer no longer uses a `TextBox` as the editor surface. `MonacoEditor` paints through Win2D `CanvasSwapChainPanel`, measures text with DirectWrite-backed `CanvasTextLayout`, and feeds real measured widths into the native `MonacoViewModel` wrapping projection. The native `MonacoTextModel` remains authoritative for document state and undo/redo.

The renderer includes viewport painting, layout caching, incremental invalidation, selection/caret rendering, pointer hit testing, clipboard/basic keyboard editing, whitespace/indent guides, bracket visuals, overview ruler, minimap, high-contrast palette handling and device-lost recovery.

The normal desktop build now uses the Native Input Windows TSF bridge as its only text-service backend. Text remains in the native `TextModel`/PieceTree while WinUI supplies host focus, selection, screen geometry and composition-decoration callbacks. The hidden TextBox proxy and experimental CoreText controller have been removed from the production renderer source; `CharacterReceived` remains only as a basic direct-character safety path. Accessibility support includes UIA Text/Text2/TextEdit, portable accessibility ranges, screen-reader eventing, and pseudolocalization.

Run this on Windows for native CTest, the full solution build, and managed/PInvoke integration tests:

```powershell
./scripts/test-windows.ps1 -Configuration Release
```


## Commands and transactions

`MonacoKeybindingService` exposes the native context-key/keybinding resolver to .NET. The WinUI renderer owns managed command delegates through `EditorCommandService`, so shortcuts and `ContextFlyout` menu items share the same `when` expressions. `MonacoTextModel.BeginUndoTransaction()` returns an explicit-completion scope: call `Complete()` to commit; disposal without completion rolls back the transaction.


## Localization and IME input

The WinUI control now exposes runtime UI localization backed by the `MonacoNative.I18n` namespace in `MonacoNative.EditorServices`:

```csharp
Editor.UiLanguage = "zh-CN";
string copy = Editor.GetLocalizedString("editor.action.clipboardCopyAction");
```

Use Monaco/VS Code command IDs as keys whenever the operation exists upstream. Do not introduce a second translated label for the same operation. Part 1 ships `en-US` and `zh-CN`.

The renderer exposes `IsImeAvailable`, `IsImeComposing`, `ImeCapability`, and `InputLanguageTag`. The Windows TSF backend reads/writes the same native TextModel/PieceTree, the editor supplies real Cursor/selection state plus WinUI/Win2D ACP screen geometry, native composition is projected into the existing composition decoration/undo path, and WinUI key events are offered to `ITfKeystrokeMgr` before Monaco handling. No hidden TextBox/CoreText input backend remains in the production renderer source. `CharacterReceived` remains a basic safety path when TSF does not claim a direct character.


## Language services

`MonacoEditor.Languages` exposes `MonacoLanguageService`. Register languages/configuration/Monarch definitions and completion, hover, signature-help, inlay-hint or code-action providers without touching the native ABI. `RefreshInlayHintsAsync` projects provider hints as visual injected text; `ApplyCodeActionAsync` applies text edits as one native undo transaction.

## Native DiffEditor

The tree now includes a native `MonacoDiffEditor` baseline backed by the portable advanced diff engine: side-by-side and inline views, synchronized scrolling, per-change revert, hide-unchanged regions, inner-change highlighting and moved-block detection. See `docs/BUILDING.md` and [`docs/NATIVE_ABI.md`](docs/NATIVE_ABI.md) for the current interface contract and update policy.


## LSP integration

Create and initialize an `LspClient`, then attach it to `MonacoEditor`; the editor does not own the client lifecycle:

```csharp
await using var client = new LspClient(
    new StdioLspTransport("your-language-server", new[] { "--stdio" }));

await client.InitializeAsync(new LspInitializeOptions
{
    RootUri = new Uri("file:///C:/work/project/"),
    ClientName = "My MonacoNative Host",
});

Editor.DocumentUri = new Uri("file:///C:/work/project/Program.cs");
Editor.Model.LanguageId = "csharp";
await Editor.AttachLanguageServerAsync(client, LanguageSelector.ForLanguage("csharp"));

// Before disposing the client:
await Editor.DetachLanguageServerAsync();
await client.ShutdownAsync();
```

The same provider surface powers completion, hover, signature help, definitions, references, rename, formatting, diagnostics, full semantic tokens, inlay hints and code actions. TCP, named-pipe and caller-owned stream transports are also available. See `docs/TESTING.md` for capability boundaries.


## NuGet package consumption

The release package is `MonacoNative.WinUI3`. It carries both x64 and ARM64 native core assets; consumers must choose one of those architectures through `Platform`/`PlatformTarget` or `RuntimeIdentifier`.

```xml
<ItemGroup>
  <PackageReference Include="MonacoNative.WinUI3" Version="0.1.0" />
</ItemGroup>
```

The package keeps Windows App SDK pinned to `2.4.0`. The sole supported native ABI is **1.4.0**, recorded in `reference/native-abi.json`. `MonacoNativeAbi.EnsureCompatible()` requires an exact managed/native ABI match at startup; ship both binaries from the same build or NuGet package rather than mixing versions. See `docs/BUILDING.md` and [`docs/NATIVE_ABI.md`](docs/NATIVE_ABI.md) for the current interface contract and update policy.

## Native Input 5 TSF-only status

The temporary Native TSF/TextBox A/B selector has been retired. The SampleApp exposes theme and UI-language selectors only, and the renderer has one production input adapter: Native TSF. See `docs/TESTING.md`.


### Monaco parity and release validation

The production editor remains TSF-only. The Monaco 0.57 pinned source/work-count gate and strict Windows renderer certification path. A missing Windows renderer artifact is never treated as PASS.
