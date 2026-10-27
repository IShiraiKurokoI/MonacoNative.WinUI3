# Language services

[Home](../README.md) · [Design](TECHNICAL_NOTES.md) · [Upstream and licenses](UPSTREAM.md) · [Testing](TESTING.md)

## Registration

`MonacoNative.Language` is the shared interface layer. Each built-in language lives in a separate package. The base service registers plaintext; `MonacoEditor` installs the built-in packages, while standalone hosts can install only the packages they use.

```csharp
using MonacoNative.Language;
using MonacoNative.Language.Json;

var languages = new MonacoLanguageService();
using var registration = languages.Packages.Install(new JsonLanguagePackage());
// Supply an ILanguageTextModel document and a LanguagePosition from the host.
// await languages.ProvideCompletionItemsAsync(document, caret, new CompletionContext());
```

Built-ins implement `ILanguagePackage` and register providers directly. Semantic language services do not require an external language-server process; optional C++ formatting invokes the locally installed `clang-format` tool. `SourcePortStatus` is `PartialPort` for JSON / HTML / CSS / TS and `InProcessUpstream` for C# / C++. No package is marked `CompletePort`. JSX / TSX retain basic highlighting and keyword contributions only.

An authoritative provider's empty completion result is valid and does not get padded with fallback keywords. A missing native library produces an explicit diagnostic and permits existing fallback behavior; that fallback is not successful semantic analysis.

## Implemented scope and missing features

| Language | Current implementation | Main gaps |
| --- | --- | --- |
| JSON / JSONC | Scanner, tolerant tree, local formatting/edits, offline Schema completion/hover/diagnostics, folding; `oneOf`/`anyOf` validation, `if`/`then`/`else`, dependency rules, and conservative Schema-based quick fixes | Full JSON Schema draft behavior, evaluated/unevaluated vocabulary, and upstream branch scoring |
| HTML | Tolerant tag tree, completion, custom data, hover, paired-tag rename/ranges, folding, formatting, embedded CSS/JS diagnostics and semantic token spans, enumerated-attribute warnings and quick fixes, and safe closing-tag repairs | Complete HTML data, advanced entity/path support, and a live paired-tag editing session in the editor UI |
| CSS / SCSS / LESS | Three scanners, structural declaration tree, property/custom-variable completion, custom data, hover/definition, folding, document/range formatting; basic CSS diagnostics | Full upstream AST/semantics and dialect parsing, SCSS/LESS structural diagnostics |
| JS / TS | C++ scanning/preprocessing; basic declarations/scopes/members; arrow functions; signature help; folding; lexical/bracket diagnostics; simple snapshot imports; document/range formatting for JS/TS | Basic syntax diagnostics are still limited. npm/package resolution, node_modules scanning, full type checking, and complete JSX/TSX semantics are outside the supported scope. |
| C# | In-process Roslyn completion, Quick Info, source references, formatting, compiler diagnostics, signature help, symbol rename across supplied workspace snapshots, organize usings, duplicate-using quick fix, classified semantic tokens and optional parameter/inferred-local-type inlay hints | Full Roslyn code-fix catalog and solution/project loading, source generators, metadata-as-source, full VS integration |
| C++ | In-process libclang completion, parameter snippets, diagnostics, hover, declaration/definition navigation, signature help and overloads, active-file references and rename, include diagnostics, missing-semicolon quick fix, explicit compile arguments and optional clang-format | Project-wide indexing/rename, automatic include insertion, and a full Clang code-fix engine |

Formatting for HTML, CSS/SCSS/LESS and JS/TS uses the shared line/brace formatter. C++ uses LLVM clang-format, JSON keeps its JSONC formatter, and C# uses Roslyn. Providers return ordinary workspace/text edits through the existing host APIs; changes to other files are only applied through the host workspace-edit callback.

## C#: official precompiled Roslyn

`Microsoft.CodeAnalysis.CSharp.Features` is fixed at **[4.11.0]**, restored by NuGet and called in process. The source-ProjectReference switch has been removed. No Roslyn Language Server process is required. The managed host targets .NET 10; the service currently uses C# 12 parsing defaults.

`CSharpLanguagePackage.Options` accepts `CSharpLanguageOptions`:

| Option | Purpose |
| --- | --- |
| `Documents` | Additional source snapshots in the same in-memory workspace |
| `MetadataReferencePaths` | Explicit assembly references |
| `PreprocessorSymbols` | Conditional compilation symbols |
| `GlobalUsings` | Host-provided global using directives |
| `IncludeRuntimeReferences` | Include basic runtime references by default, enabling examples such as `Console.` |
| `LanguageVersion` | Explicit parser language version |

The provider does not implicitly parse `.csproj` files, traverse disk, or execute source generators during a request. Navigation returns actual source locations, not fabricated library source files. Signature help adapts method groups; it is not a port of Roslyn's complete internal signature service.

The rename provider uses `Renamer.RenameSymbolAsync` on the supplied in-memory Roslyn
workspace, returning workspace edits only for known document URIs. The code-action
provider offers source-level **Organize usings** and a conservative duplicate-using
fix. It does not expose every Roslyn analyzer/code-fix provider. Roslyn classifications
feed semantic tokens. Inlay hints are provided on request for positional call
arguments and inferred local variable types; hosts choose when to refresh or display
them. Signature overload matching uses Roslyn's existing semantic argument typing and
the editor's active-argument selection.

Completion acceptance requires Roslyn's authoritative resolved edits. Failed resolution does not fall back to inserting the display label. URI, language, version, or caret changes invalidate asynchronous results.

## C++ formatting with LLVM clang-format

`libclang` handles parsing and semantic analysis; it does not expose LLVM's
LibFormat API. The C++ document/range formatting providers therefore use the
standard `clang-format` executable with source passed through standard input.
This does not modify files on disk, start a language server, or depend on a
heuristic brace-counting formatter. The tool is optional and is not distributed
with the `libclang.runtime` packages.

Resolution order: `CppLanguageOptions.ClangFormatPath`, the
`MONACONATIVE_CLANG_FORMAT` environment variable, an executable under
`CppLanguageOptions.ToolchainRoot/bin`, and finally the process `PATH`.
An absent tool returns no edits. The command uses `--style=file` and
`--fallback-style=Microsoft`, honors the nearest `.clang-format` configuration,
and uses `--lines=start:end` for range formatting. The document URI supplies
the assumed filename and working directory. Configure `.clang-format` for
indentation/style rather than relying on the generic formatting options.

```csharp
var package = new CppLanguagePackage
{
    Options = new CppLanguageOptions { ClangFormatPath = @"C:\LLVM\bin\clang-format.exe" }
};
```

LLVM's [clang-format reference](https://clang.llvm.org/docs/ClangFormat.html)
documents the command-line contract and available styles.

## C++: precompiled libclang and the existing bridge

`libclang.runtime.win-x64` and `libclang.runtime.win-arm64` are both fixed at **[17.0.4]**. The managed library remains AnyCPU; the consumer's RID selects native assets, not the build machine's architecture. The adapter is compiled into the existing `MonacoNative.Core.Native`, without another LLVM project or server.

At runtime, the locator checks the application directory and `runtimes/win-{x64,arm64}/native/libclang.dll` against the current process architecture. `CppLanguageOptions.LibClangPath` / `MONACONATIVE_LIBCLANG` remain explicit overrides. Legacy `ToolchainRoot` and version/architecture receipts remain supported, but normal builds do not require GitHub checkouts or upstream compilation.

Windows build and publish targets inspect the deployed DLL's PE structure, selected architecture, and all 51 symbols currently required by the bridge. This is separate from the existing runtime check. It cannot prove loader dependencies, returned ABI values, or semantic behavior; see [building](BUILDING.md).

The NuGet runtime is not a complete C++ toolchain. `MonacoClangResourceDirectory` can supply matching resources, including `include/stddef.h`; the existing target copies them to `clang/lib/clang/17/include`. The host still supplies system/STL/project includes, macros, and target arguments.

`CompilerArguments` defaults to `-xc++ -std=c++20`. `Documents` supplies URI-to-unsaved-source/header snapshots; `CompilerArgumentsProvider` can override per document. `CompilationDatabasePath` reads the **arguments array** form of `compile_commands.json`, strips compiler/output arguments, and preserves the working directory. It does not evaluate shell `command`, launchers, plugins / `-Xclang`, or response files. Use trusted explicit arguments for other configurations.

Completion preserves Clang availability and priority, converts parameter chunks into numbered snippets, and maps kinds to existing Codicons. The signature provider queries libclang callables and overload cursors, returning parameter labels and the active argument index. Navigation returns available declarations and definitions. Rename only edits libclang-resolved references in the active file; it is not a workspace refactor. Clang reports missing includes via diagnostics; only conservative missing-semicolon diagnostics currently produce a quick fix. Navigation converts target-snapshot UTF-8 offsets to UTF-16, including CRLF and non-BMP characters. References are limited to the active file in the translation unit, not a project index. Cancellation prevents result application but does not forcibly interrupt synchronous libclang calls. Loading/configuration errors return `NATIVE_UNAVAILABLE`.

The C++ sample avoids standard-library headers to make basic custom-member checks easier. Validate the actual Windows package through [required integration](TESTING.md); a Linux test using an installed libclang is not equivalent.

## JSON / JSONC

Scanning, formatting, tolerant parsing, tree, and location modules have been translated. `JsoncEdit.Modify` performs local insert/replace edits; `Remove` explicitly means deletion, while C# `null` remains JSON null. Host values are serialized with `System.Text.Json`. Only affected regions are formatted; the original document is not entirely reserialized.

Formatting preserves comments, original tokens, and line endings. `FormattingOptions.Extra` supports `keepLines` and `eol`. Parsing has bounded nesting and cancellation checks; public positions use UTF-16. Out-of-range host edits throw explicit exceptions.

```csharp
// The host supplies schemaJson and document patterns.
// package.Schemas.Register(schemaUri, schemaJson, filePatterns);
```

Schema resolution uses only explicitly registered data. The implemented subset includes `$schema` selection, local JSON Pointers, references across registered schemas, type / properties / required / additionalProperties / patternProperties / items / prefixItems / enum / const / default, common string/numeric bounds, `allOf` / `anyOf` / `oneOf` / `not`, `if` / `then` / `else`, `dependentRequired` and `dependentSchemas`. Conditional branches are selected against the current document for nested completion and quick fixes. Quick fixes can insert required properties using Schema defaults or safe type fallbacks and can replace invalid enum/const values while retaining surrounding JSONC comments.

Anchors/dynamic references, evaluated/unevaluated constraints, format assertions, uniqueItems/contains, and other full-vocabulary behavior remain missing. Completion merges possible branches rather than reproducing all upstream branch scoring. No automatic network or disk Schema loading occurs.

## HTML / CSS custom data and tag editing

`HtmlLanguagePackage.Data.Register(customDataJson)` supports tags, attributes, and value sets. `CssLanguagePackage.Data.Register(customDataJson)` supports property descriptions and property-specific values. Default data is limited, not the complete upstream Web dataset.

HTML pairing, linked ranges, and tag rename come from identified upstream modules. Computing linked ranges is not yet a live two-end editing UI. Embedded `<script>` and `<style>` regions delegate diagnostics and semantic tokens to registered JavaScript/CSS providers through virtual document snapshots. Standalone use falls back to bounded lexical highlighting. Embedded diagnostics are remapped to HTML positions; the embedding does not parse npm dependencies. Enumerated values from host-provided HTML custom data are validated conservatively. Quick fixes replace known invalid values or repair simple unclosed/unmatched tags without rewriting the complete document.

The CSS structural declaration layer is project code built on ported scanners, not the full upstream parser. SCSS/LESS scanners recognize variables, interpolation, and their lexical extensions. LESS backticks are scanned, not executed as JavaScript. Structural diagnostics currently register only for CSS.

## JS / TS: basic editing scope

The scanner targets **TypeScript 5.8.3**: common tokens, escapes, numeric literals, Unicode identifiers, comments, template/greater-than/regex rescanning, parser-facing JSX and JSDoc scanning, speculative scanning, conflict markers, and unterminated-regex recovery. Full regex syntax/flag validation, comment-directive state, some JSDoc modes, and full unpaired-surrogate fidelity are still missing. Scanner APIs do not imply a complete JSX/TSX parser, binder, or checker.

Basic declarations cover common classes/interfaces/type-object forms, object literals, functions/arrows/function expressions, methods, parameters, local scopes/shadowing, static/instance distinction, some inherited members, optional access, member chains, and simple return types. Recognized classes support `this.property = ...`; common semicolon-free arrow scopes are handled. Unknown receivers do not get fabricated members.

Selected TypeScript standard-library declarations provide Console / String / Array / Number / Math / JSON member data. This is data extraction, not full `lib.*.d.ts` type checking. Constructors and default/optional/rest parameter labels feed snippets and signature help. Completing a call name with existing parentheses does not insert another pair.

Signature help tracks nested calls, arrays, objects, strings, templates, comments, and regex. Overloads are selected by **argument count**, not full type resolution. JS candidates exclude TS-only keywords. Diagnostics are lexical and bracket-structural, not a complete basic parser or type checker.

### Explicit snapshots and dependency preprocessing

`TypeScriptLanguageOptions.Documents` copies input snapshots; `SetDocuments(...)` replaces them atomically. `DocumentsProvider` takes precedence and should return a stable snapshot. The engine neither reads files itself nor runs Node. Named/aliased/default/type-only/namespace imports, common exports/re-exports, and relative extension/index lookup operate only on host-provided snapshots.

Same-named types retain module identity. Non-exported/private members and ambiguous star exports are excluded. Cycles and graph size are bounded. Anonymous default declarations, full cyclic inference, generics, unions/intersections, control-flow narrowing, and complete destructuring are not implemented. Complex node_modules, package exports, NodeNext/Bundler, tsconfig mappings, and incremental project caches are outside this phase.

`TypeScriptNativeLanguageService.PreProcessFileAsync(model, readImportFiles: true, detectJavaScriptImports: null, cancellationToken: ...)` returns file/type/lib references, imports, ambient modules, isLibFile, and snapshot URI/version. JS detection defaults from the language ID. JSX/TSX can use this API without implying full semantic support.

Preprocessing `Pos` / `End` preserve upstream **zero-based UTF-16** conventions. An import's Pos starts at the quote, while End relates to its decoded name length; these are not directly usable edit ranges. The module service separately records raw string boundaries to handle CJK, emoji, and escapes correctly.

Static/dynamic imports, JS require/AMD strings, and triple-slash path references can use snapshot path completion/navigation. Candidates use ordinary InsertText and File/Folder Codicons, replace the complete path, and preserve quotes; they are not interpreted as snippets. Quotes, backticks, and `/` are triggers. Package/type/lib names and incomplete escaped prefixes are not guessed. Added/removed snapshots affect subsequent requests.

## Folding and signature-help integration

`RegisterFoldingRangeProvider` / `ProvideFoldingRangesAsync` use **one-based inclusive** `LanguageFoldingRange` coordinates. `null` means not applicable; an empty list means no folds and must not trigger generic fallback. JSON/HTML/CSS folding has explicit upstream provenance; JS/TS token folding is a basic project implementation.

JSON `RangeLimit=0` returns empty and calls the limit callback when exceeded. HTML/CSS preserve upstream `||` semantics: zero means unspecified. Negative limits are rejected. The host may supply HTML void-element data, copied for worker use; this is not a complete HTMLDataManager port. Unload/disposal cancels pending folding.

```csharp
var help = await languages.ProvideSignatureHelpAsync(model, position,
    new SignatureHelpContext(SignatureHelpTriggerKind.Invoke), cancellationToken);
var folds = await languages.ProvideFoldingRangesAsync(model,
    new FoldingContext(RangeLimit: 500), cancellationToken);
```

`Editor.ParameterHintsEnabled` controls automatic signature help. `(` / `,` trigger it, `)` refreshes it, `Ctrl+Shift+Space` invokes it, and `Esc` closes it. Parameter hints and completion use independent non-focus-stealing overlays. Function snippet acceptance can request hints. While hints are visible, `Alt+Down` and `Alt+Up` navigate overloads; a manually selected signature is retained when its label is present in the next provider result. Not all languages register signature providers.

## Snippets, ranking, and Codicon

`Ctrl+Space` invokes completion; `SuggestOnTypeEnabled` controls delayed automatic triggering. Candidates support arrows, Enter/Tab, Esc, and page navigation. Snippet sessions use UTF-16 offsets. Tab/Shift+Tab navigate numbered fields, equal-number fields mirror changes, and `$0` finishes. Replacing a parent invalidates covered child fields. User edits and mirrors share undo transactions. TSF mirrors run after write-lock/selection updates and still need Windows regression coverage.

When suggestions are open, Enter/Tab accepts the candidate; Shift+Tab preserves snippet navigation. Esc first closes suggestions, then cancels the snippet on another press. Moving outside the active field, replacing the document, or undoing cancels the session. Choice placeholders offer a non-focus-stealing list with Up/Down, Home/End, Enter, Tab, Escape and pointer selection. The chosen value updates linked placeholders in the same undo transaction. A new snippet replaces the old session rather than nesting sessions.

Regex transforms use bounded .NET regex, not the complete JS regex engine. Common file/line/date variables exist, not every VS Code variable.

```csharp
Editor.SuggestOptions.Snippets = SnippetSortOrder.Inline; // Top / Bottom / Hidden
Editor.SuggestOptions.Selection = SuggestSelectionMode.RecentlyUsedByPrefix;
Editor.SuggestOptions.GracefulFiltering = true;
Editor.SuggestOptions.MatchOnWordStartOnly = true;
Editor.SuggestReplaceMode = false;
```

Ranking ports VS Code dynamic-programming fuzzy matching, case/word-boundary bonuses, gap penalties, and bounded adjacent-transposition fallback. It uses filterText, per-item edit starts, sortText, stable ties, and snippet placement. Initial selection additionally uses preselect, recent choices, and longest-prefix memory. Memory is per editor instance, not persisted across processes. `WordDistance` is an optional host hook, not automatic semantic-scope distance.

Codicons use Microsoft's original vector outlines converted to WinUI Path, without shipping a font. Methods/functions/constructors preserve the upstream shared glyph aliases. Browser-equivalent rasterization is not promised.

Completion items with explicit commit-character metadata can be accepted on punctuation in the direct `CharacterReceived` path; the completion and punctuation form one undo transaction without duplicate terminal punctuation. Active native TSF composition remains owned by TSF, and TSF-delivered commit characters are not intercepted as completion commands. The legacy LSP client therefore does not advertise complete commit-character support.

## Legacy LSP compatibility

Generic transports, `AttachLanguageServerAsync`, and `ConnectLanguagePackageServerAsync(ILspLanguagePackage, ...)` are explicit host options only. Built-ins and the sample do not call them. The old `new JsonLanguagePackage { Server = ... }` form no longer applies.

For explicit connections, completion resolve preserves opaque data, combines document/import edits, validates overlap, and recalculates snippet starts. Incomplete lists use trigger kind 3 only during continued typing. The editor handles its own completion/signature commands locally and forwards other commands. Retaining these APIs is not source-port work.

## Test provenance and acceptance

| Test source | Provenance / scope | Runner |
| --- | --- | --- |
| `UpstreamPortTests.Json.cs` | node-jsonc-parser edit assertions and scanner subset | C# LanguageTests |
| `UpstreamPortTests.Html.cs` | Upstream rename / matchingTagPosition / linkedEditing files | C# LanguageTests |
| `UpstreamPortTests.Css.cs` | CSS / LESS scanner assertions; additional project SCSS regressions | C# LanguageTests |
| `UpstreamPortTests.Folding.cs` | 73 upstream expectations: 16 JSON, 19 HTML, 38 CSS/dialects; 5 host cases | C# LanguageTests |
| `UpstreamScannerTests.cpp` | 201 frozen TypeScript differential fixtures, 6,476 token records | C++ NativeAbi |
| `ScannerApiTests.cpp` | 121 frozen comparisons, 506 states, plus boundary checks | C++ NativeAbi |
| `PreProcessTests.cpp` | 41 upstream assertions, 228 differential cases, 3 boundary cases, 40 integration checks, 32 concurrent and 512 malformed-input checks | C++ NativeAbi |
| `BasicEditingTests.cpp` | Project basic-editing regressions, not upstream fourslash tests | C++ NativeAbi |
| `NativeBinaryValidationTests.cs` | Project synthetic-PE tests of the shared build-time inspector | C# LanguageTests |

Translated tests do not need Node. Optional Python differential tools use matching official TS as a development oracle, not an editor runtime dependency. The TypeScript 5.8.3 oracle SHA-256 is recorded in source and the [lock file](../reference/upstream-sources.lock.json).

Managed tests also cover snapshots, UTF-16/CRLF, registration/disposal, cancellation, diagnostics, Schema, coordinates, bridges, Roslyn semantics, and formatting. Native success does not establish managed compilation, Windows deployment, TSF, or WinUI correctness. A strict entry point becomes evidence only after execution. See [testing](TESTING.md) for commands and SKIP versus required dependencies.

## Package consumption

The aggregate `MonacoNative.WinUI3` project version is `0.1.0`, a repository packaging setting rather than a claim of public publication. It contains x64/ARM64 native assets. Consumers select the matching Platform / PlatformTarget or RID. Windows App SDK is fixed at 2.4.0; native and managed components must come from the same build.

```xml
<!-- Use only after building this package and placing it on an available NuGet feed. -->
<PackageReference Include="MonacoNative.WinUI3" Version="0.1.0" />
```
