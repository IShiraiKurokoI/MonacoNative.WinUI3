# Upstream sources, dependencies, and licenses

[Home](../README.md) · [Design](TECHNICAL_NOTES.md) · [Language services](LANGUAGE_SERVICES.md) · [Third-party notices](../THIRD_PARTY_NOTICES.md)

This file records upstream implementations, precompiled libraries, translated tests, versions, local files, and licenses. The project MIT license does not replace third-party licenses or attribution.

The existing provenance inventory was reviewed on **2026-10-25**. Version records come from source headers, notices, and [upstream-sources.lock.json](../reference/upstream-sources.lock.json). The English documentation revision preserves those records; it does not invent commit IDs for early work that recorded only a date or `main`. A current `main` link locates a source, not a reproducible historical snapshot.

## Overview

| Upstream project | Use and recorded version | License / upstream text | Local text |
| --- | --- | --- | --- |
| [Monaco Editor](https://github.com/microsoft/monaco-editor) | Recorded 0.57.0 compatibility reference; no browser runtime bundled | [MIT](https://github.com/microsoft/monaco-editor/blob/main/LICENSE.txt) | [Monaco-MIT](licenses/Monaco-MIT.txt) |
| [Visual Studio Code](https://github.com/microsoft/vscode) | Editor-core baseline in reference data; partial C# completion-algorithm ports, whose early review did not pin a full commit | [MIT](https://github.com/microsoft/vscode/blob/main/LICENSE.txt) | [VSCode-MIT](licenses/VSCode-MIT.txt) |
| [node-jsonc-parser](https://github.com/microsoft/node-jsonc-parser) | Early scanner/parser/formatter not commit-pinned; edit module and added tests use v3.3.1 | [MIT · v3.3.1](https://github.com/microsoft/node-jsonc-parser/blob/v3.3.1/LICENSE.md) | [Microsoft-MIT](licenses/Microsoft-MIT.txt) |
| [vscode-json-languageservice](https://github.com/microsoft/vscode-json-languageservice) | v5.4.1 folding and tests, not a full Schema engine port | [MIT and bundled third-party notices](https://github.com/microsoft/vscode-json-languageservice/blob/v5.4.1/LICENSE.md) | [JSON-LanguageService-LICENSE](licenses/JSON-LanguageService-LICENSE.txt) |
| [vscode-html-languageservice](https://github.com/microsoft/vscode-html-languageservice) | Early scanner/parser/completion subsets not commit-pinned; tag editing, folding, and tests use v5.5.0 | [MIT · v5.5.0](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/LICENSE.md) | [Microsoft-MIT](licenses/Microsoft-MIT.txt) |
| [vscode-css-languageservice](https://github.com/microsoft/vscode-css-languageservice) | Early CSS scanner not commit-pinned; SCSS/LESS scanning, folding, and tests use v6.3.1 | [MIT · v6.3.1](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/LICENSE.md) | [Microsoft-MIT](licenses/Microsoft-MIT.txt) |
| [TypeScript](https://github.com/microsoft/TypeScript/tree/v5.8.3) | 5.8.3 scanning, preprocessing, selected editing helpers, and declaration data translated to C++ | [Apache-2.0](https://github.com/microsoft/TypeScript/blob/v5.8.3/LICENSE.txt), with upstream third-party notices retained | [License](licenses/TypeScript-Apache-2.0.txt) / [third-party notices](licenses/TypeScript-ThirdPartyNotices.txt) |
| [Roslyn](https://github.com/dotnet/roslyn) | Official precompiled `Microsoft.CodeAnalysis.CSharp.Features [4.11.0]`, in process | [MIT · package source commit](https://github.com/dotnet/roslyn/blob/5e3a11e2e7f952da93f9d35bd63a2fa181c0608b/License.txt) | [Roslyn-MIT](licenses/Roslyn-MIT.txt) |
| [LLVM / Clang](https://github.com/llvm/llvm-project) | Precompiled libclang 17.0.4 x64/ARM64; bridge C declarations referenced llvmorg-17.0.6 | [Apache-2.0 WITH LLVM-exception](https://github.com/llvm/llvm-project/blob/llvmorg-17.0.4/llvm/LICENSE.TXT) | [Apache-2.0](licenses/TypeScript-Apache-2.0.txt) + [LLVM exception](licenses/LLVM-exception.txt); retain the package's full LICENSE.TXT |
| [Microsoft Codicons](https://github.com/microsoft/vscode-codicons) | Vector subset converted to WinUI Path; extraction date recorded, no immutable icon commit | [Graphics: CC BY 4.0](https://github.com/microsoft/vscode-codicons/blob/main/LICENSE); [upstream code: MIT](https://github.com/microsoft/vscode-codicons/blob/main/LICENSE-CODE) | [CC BY 4.0 text](licenses/CC-BY-4.0.txt); attribution and changes below |

The corresponding node-jsonc-parser / HTML / CSS licenses share the local Microsoft MIT text, retaining the copyright line. The JSON language-service license also contains a glob-to-regexp notice. It is retained without implying that its implementation has been ported.

Local TypeScript license/notices and the LLVM exception were separated from existing distribution notices; the CC BY 4.0 legal text was likewise retained from the existing notice. Some earlier copies adjusted line wrapping and are not claimed to be byte-identical downloads. This revision leaves the local license files and root notices unchanged. Codicon attribution and modifications remain below and in the root notice. LLVM distributions may include legacy/third-party terms; do not redistribute binaries with only the exception excerpt.

## Source and test mappings

### VS Code editor algorithms

- [filters.ts](https://github.com/microsoft/vscode/blob/main/src/vs/base/common/filters.ts), [completionModel.ts](https://github.com/microsoft/vscode/blob/main/src/vs/editor/contrib/suggest/browser/completionModel.ts), [suggest.ts](https://github.com/microsoft/vscode/blob/main/src/vs/editor/contrib/suggest/browser/suggest.ts), and [suggestMemory.ts](https://github.com/microsoft/vscode/blob/main/src/vs/editor/contrib/suggest/browser/suggestMemory.ts) → [CompletionRanking.cs](../src/MonacoNative.Language/CompletionRanking.cs). Partial fuzzy scoring/ranking/memory ports, with C# storage, per-call buffers, and host adaptations. The original review date is 2026-10-22; no fixed algorithm commit was recorded.
- The editor baseline records `6a598d4a13031703d483d103c1d934a36ad27971`. Its bracketPairsTree/combineTextEditInfos.ts adaptation maps to [CombineTextEditInfos.cs](../src/MonacoNative.Language/BracketPairs/CombineTextEditInfos.cs). See the [core baseline](../reference/monaco-0.57.0-baseline.json). That reference must not be used to imply that all later modules share the same source version.
- C# snippet parsing and session management are project implementations. Their tests are not the complete upstream VS Code snippet suite.

### JSON / JSONC

| Upstream source / tests | Local implementation |
| --- | --- |
| node-jsonc-parser `src/impl/scanner.ts`, `format.ts`, `parser.ts` (early main review, no pinned commit) | [JsoncScanner.cs](../src/MonacoNative.Language.Json/JsoncScanner.cs), [JsoncFormatter.cs](../src/MonacoNative.Language.Json/JsoncFormatter.cs), [JsoncParser.cs](../src/MonacoNative.Language.Json/JsoncParser.cs) |
| [v3.3.1 edit.ts](https://github.com/microsoft/node-jsonc-parser/blob/v3.3.1/src/impl/edit.ts) | [JsoncEdit.cs](../src/MonacoNative.Language.Json/JsoncEdit.cs) |
| [v3.3.1 edit.test.ts](https://github.com/microsoft/node-jsonc-parser/blob/v3.3.1/src/test/edit.test.ts) and the scanner portion of [json.test.ts](https://github.com/microsoft/node-jsonc-parser/blob/v3.3.1/src/test/json.test.ts) | [UpstreamPortTests.Json.cs](../tests/LanguageTests/UpstreamPortTests.Json.cs) |
| [v5.4.1 jsonFolding.ts](https://github.com/microsoft/vscode-json-languageservice/blob/v5.4.1/src/services/jsonFolding.ts) and [folding.test.ts](https://github.com/microsoft/vscode-json-languageservice/blob/v5.4.1/src/test/folding.test.ts) | [JsonFolding.cs](../src/MonacoNative.Language.Json/JsonFolding.cs) and JSON assertions in [UpstreamPortTests.Folding.cs](../tests/LanguageTests/UpstreamPortTests.Folding.cs) |

The C# adaptation distinguishes `Remove` from null, adds cancellation/nesting/range checks, and serializes host values with System.Text.Json. The Schema registry, subset validation, and host providers are project integration, not a full vscode-json-languageservice source port.

### HTML

Early `htmlScanner.ts`, `htmlParser.ts`, and context subsets of `htmlCompletion.ts` map to [HtmlScanner.cs](../src/MonacoNative.Language.Html/HtmlScanner.cs), [HtmlParser.cs](../src/MonacoNative.Language.Html/HtmlParser.cs), and [HtmlSyntaxCompletionProvider.cs](../src/MonacoNative.Language.Html/HtmlSyntaxCompletionProvider.cs). The record is a main review on 2026-10-23, not a full commit pin.

The v5.5.0 [htmlRename.ts](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/src/services/htmlRename.ts), [htmlMatchingTagPosition.ts](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/src/services/htmlMatchingTagPosition.ts), and [htmlLinkedEditing.ts](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/src/services/htmlLinkedEditing.ts) map to [HtmlTagEditing.cs](../src/MonacoNative.Language.Html/HtmlTagEditing.cs). Corresponding rename / matchingTagPosition / linkedEditing assertions map to [UpstreamPortTests.Html.cs](../tests/LanguageTests/UpstreamPortTests.Html.cs).

The v5.5.0 [htmlFolding.ts](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/src/services/htmlFolding.ts) and [folding.test.ts](https://github.com/microsoft/vscode-html-languageservice/blob/v5.5.0/src/test/folding.test.ts) map to [HtmlFolding.cs](../src/MonacoNative.Language.Html/HtmlFolding.cs) and the shared C# folding tests. Adaptations include one-based positions, snapshots, cancellation, and explicit void-element data, not a complete data-manager port.

### CSS / SCSS / LESS

The early `cssScanner.ts` port maps to [CssScanner.cs](../src/MonacoNative.Language.Css/CssScanner.cs); its main review did not pin a commit. The v6.3.1 [scssScanner.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/parser/scssScanner.ts) / [lessScanner.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/parser/lessScanner.ts) map to [CssDialectScanner.cs](../src/MonacoNative.Language.Css/CssDialectScanner.cs). [css/scanner.test.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/test/css/scanner.test.ts) / [less/scanner.test.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/test/less/scanner.test.ts) map to [UpstreamPortTests.Css.cs](../tests/LanguageTests/UpstreamPortTests.Css.cs).

The v6.3.1 [cssFolding.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/services/cssFolding.ts) / [folding.test.ts](https://github.com/microsoft/vscode-css-languageservice/blob/v6.3.1/src/test/css/folding.test.ts) map to [CssFolding.cs](../src/MonacoNative.Language.Css/CssFolding.cs) and shared folding tests. Project SCSS regressions, the CSS structural tree, and the host custom-data layer are separately identified; they are not a complete upstream AST/semantic service.

### TypeScript / JavaScript

The fixed reference is 5.8.3, source commit `68cead182cc24afdc3f1ce7c8ff5853aba14b65a`. The independent `lib/typescript.js` oracle has SHA-256:

```text
dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0
```

| Upstream | Local source | Classification |
| --- | --- | --- |
| [scanner.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/compiler/scanner.ts), token/flag/Unicode data from [types.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/compiler/types.ts) | [TypeScriptScanner.h](../src/MonacoNative.Language.TypeScript/Native/TypeScriptScanner.h), [TypeScriptTables.h](../src/MonacoNative.Language.TypeScript/Native/TypeScriptTables.h) | C++ port with UTF-8/UTF-16, state-restoration, and boundary adaptations |
| Selected declarations from lib.es5.d.ts / lib.dom.d.ts / lib.es2015.collection.d.ts / lib.es2015.promise.d.ts / lib.es2015.core.d.ts | [TypeScriptBuiltins.h](../src/MonacoNative.Language.TypeScript/Native/TypeScriptBuiltins.h) | Extracted declaration data; TypeScript is not executed |
| [preProcess.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/services/preProcess.ts) and reference-pragma branches of parser.ts | [TypeScriptPreProcess.h](../src/MonacoNative.Language.TypeScript/Native/TypeScriptPreProcess.h) | Dependency preprocessor port, not the whole parser |
| [preProcessFile.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/testRunner/unittests/services/preProcessFile.ts) | [PreProcessTests.cpp](../tests/NativeAbiTests/PreProcessTests.cpp) | Translated upstream assertions, independent differential cases, separately labeled host regressions |
| Frozen independent outputs from public scanner APIs | [UpstreamScannerTests.cpp](../tests/NativeAbiTests/UpstreamScannerTests.cpp), [ScannerApiTests.cpp](../tests/NativeAbiTests/ScannerApiTests.cpp) | Differential expectations expressed in C++, not the full upstream framework |
| Argument index/count/rest branches of [signatureHelp.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/services/signatureHelp.ts) and region parsing from [outliningElementsCollector.ts](https://github.com/microsoft/TypeScript/blob/v5.8.3/src/services/outliningElementsCollector.ts) | [TypeScriptBasicEditing.h](../src/MonacoNative.Language.TypeScript/Native/TypeScriptBasicEditing.h) | Partial helper adaptations; [BasicEditingTests.cpp](../tests/NativeAbiTests/BasicEditingTests.cpp) contains project regressions |

The basic declarations, scopes, snapshot imports, and path candidates in `TypeScriptDeclarations.h` / `TypeScriptModules.h` are host implementations, not renamed upstream binder/checker/module-resolver code. C++ exception recovery, bounded ranges, JSDoc trailing-hyphen termination, and reference-string recovery are adaptation changes. No Node, tsserver, or upstream JavaScript is bundled or executed.

### Precompiled Roslyn and libclang

Roslyn package: [Microsoft.CodeAnalysis.CSharp.Features 4.11.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Features/4.11.0). Recorded source commit: `5e3a11e2e7f952da93f9d35bd63a2fa181c0608b`. The package is called directly through its .NET workspace and editing APIs.

libclang packages: [win-x64 17.0.4](https://www.nuget.org/packages/libclang.runtime.win-x64/17.0.4) / [win-arm64 17.0.4](https://www.nuget.org/packages/libclang.runtime.win-arm64/17.0.4). Distribution tooling comes from [dotnet/ClangSharp](https://github.com/dotnet/ClangSharp), but this project does not switch to ClangSharp's managed APIs or apply its repository MIT license to LLVM binaries. libclang uses Apache-2.0 WITH LLVM-exception.

The minimal C declarations in [ClangLanguageEngine.cpp](../src/MonacoNative.Language.Cpp/Native/ClangLanguageEngine.cpp) reference llvmorg-17.0.6 [Index.h](https://github.com/llvm/llvm-project/blob/llvmorg-17.0.6/clang/include/clang-c/Index.h), CXString.h, and CXSourceLocation.h. This is distinct from the fixed 17.0.4 runtime packages and does not mean that complete Clang/clangd source is embedded. The old LLVM source lock `6009708b4367171ccdbf4b5905cb6a803753fe18` remains a maintenance reference.

### Codicon attribution and modifications

Graphic author: **Microsoft Corporation**. The source is [Microsoft Codicons](https://github.com/microsoft/vscode-codicons). The extraction record points to [Iconify's codicon.json distribution](https://github.com/iconify/icon-sets/blob/master/json/codicon.json), dated 2026-10-22. No immutable icon commit was recorded; I do not invent a pinned version.

SVG paths and fill rules are stored as WinUI geometry in [CodiconPresenter.cs](../src/MonacoNative.Renderer.WinUI3/CodiconPresenter.cs), preserving 16-unit outlines and upstream kind aliases. Theme settings supply colors. The outlines were not manually redrawn, and no font file is shipped. Kind mappings reference VS Code's codiconsLibrary.ts and languages.ts under MIT. Methods, functions, and constructors retain `symbol-method`.

Graphics use **CC BY 4.0**; see the [official license](https://github.com/microsoft/vscode-codicons/blob/main/LICENSE) and [Creative Commons legal text](https://creativecommons.org/licenses/by/4.0/legalcode). Upstream build code separately uses MIT. These licenses must not be conflated. The use and adaptations do not imply Microsoft endorsement or sponsorship.

## Platform dependencies, not ported code

Windows App SDK, Win2D, and C++/WinRT use the existing NuGet references. Their upstream projects are [WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK), [Win2D](https://github.com/microsoft/Win2D), and [cppwinrt](https://github.com/microsoft/cppwinrt). Keep licenses and transitive notices supplied by the restored versions. This inventory is not a complete transitive-dependency SBOM.

The build-time native inspector and its regression tests are project code under the project MIT license. They use documented PE/MSBuild interfaces; they are not another ported language service. No new third-party runtime dependency is introduced by that check.

## Distribution and maintenance

[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) remains at the repository root. `doc/licenses/` contains the local license texts. Aggregate WinUI / WinRT packages include the documentation and notices.

Early unpinned modules still need source reconciliation, immutable revisions, and differential tests to improve reproducibility. Legacy LSP compatibility is not counted as ported code.
