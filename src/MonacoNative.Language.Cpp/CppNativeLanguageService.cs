using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Language.Cpp;

/// <summary>libclang inputs and an optional clang-format executable for formatting.</summary>
public sealed class CppLanguageOptions
{
    public string? LibClangPath { get; init; }
    /// <summary>Optional path to clang-format. Otherwise toolchain bin and PATH are searched.</summary>
    public string? ClangFormatPath { get; init; }
    public string? ToolchainRoot { get; init; }
    public string? ClangResourceDirectory { get; init; }
    public IReadOnlyList<string> CompilerArguments { get; init; } = new[] { "-xc++", "-std=c++20" };
    public IReadOnlyDictionary<Uri, string> Documents { get; init; } = new Dictionary<Uri, string>();
    /// <summary>If supplied, overrides Documents. Return copied snapshots of open C/C++ files.</summary>
    public Func<IReadOnlyList<NativeLanguageDocument>>? DocumentsProvider { get; init; }
    public string? CompilationDatabasePath { get; init; }
    public Func<Uri, IReadOnlyList<string>>? CompilerArgumentsProvider { get; init; }
}

/// <summary>
/// Direct libclang C API integration. Supports actual C++ semantic completion/diagnostics and
/// translation-unit navigation, not clangd's full project index or Microsoft cpptools parity.
/// </summary>
public sealed class CppNativeLanguageService : NativeCompilerService, IReferenceProvider, IRenameProvider, ISignatureHelpProvider, ICodeActionProvider
{
    private readonly CppLanguageOptions _options;
    private readonly Lazy<CppToolchainPaths> _toolchain;
    public CppNativeLanguageService(CppLanguageOptions? options = null) : base(true)
    {
        _options = options ?? new();
        _toolchain = new(() => CppToolchain.Locate(_options));
    }
    public override IReadOnlyList<string> TriggerCharacters { get; } = new[] { ".", ">", ":", "#", "\"", "<" };
    protected override string LanguageSource => "clang-in-process";
    protected override string DefaultExtension => ".cpp";
    protected override string? CompilerLibraryPath => _toolchain.Value.LibraryPath;
    protected override IReadOnlyList<NativeLanguageDocument> GetAdditionalDocuments() =>
        _options.DocumentsProvider?.Invoke().ToArray() ??
        _options.Documents.Select(pair => new NativeLanguageDocument(pair.Key, pair.Value)).ToArray();
    protected override IReadOnlyList<string> GetArguments(Uri uri)
    {
        IReadOnlyList<string> arguments = _options.CompilerArgumentsProvider is { } provider ? provider(uri).ToArray()
            : _options.CompilationDatabasePath is { Length: > 0 } path && uri.IsFile
                ? CppCompilationDatabase.ReadArguments(path, uri.LocalPath) ?? _options.CompilerArguments
                : _options.CompilerArguments;
        var resource = _toolchain.Value.ResourceDirectory;
        if (resource is not null && !arguments.Any(value => value is "-resource-dir" or "--resource-dir" ||
            value.StartsWith("-resource-dir=", StringComparison.Ordinal) || value.StartsWith("--resource-dir=", StringComparison.Ordinal)))
            return arguments.Concat(new[] { "-resource-dir=" + resource }).ToArray();
        return arguments;
    }
    public IReadOnlyList<string> SignatureHelpTriggerCharacters { get; } = new[] { "(", "," };
    public IReadOnlyList<string> SignatureHelpRetriggerCharacters { get; } = new[] { ")" };
    public IReadOnlyList<string> ProvidedCodeActionKinds { get; } = new[] { "quickfix" };
    public async ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ILanguageTextModel model,
        LanguagePosition position, SignatureHelpContext context, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(model, position, 6, cancellationToken).ConfigureAwait(false);
        if (response is null || !response.Document.RootElement.TryGetProperty("signatures", out var values)) return null;
        var signatures = values.EnumerateArray().Select(s => new SignatureInformation(
            s.GetProperty("label").GetString() ?? "",
            Parameters: s.GetProperty("parameters").EnumerateArray()
                .Select(p => new ParameterInformation(p.GetProperty("label").GetString() ?? "")).ToArray(),
            ActiveParameter: s.GetProperty("activeParameter").GetInt32())).ToArray();
        var root = response.Document.RootElement;
        return signatures.Length == 0 ? null : new SignatureHelp(signatures,
            root.GetProperty("activeSignature").GetInt32(), root.GetProperty("activeParameter").GetInt32());
    }
    public async ValueTask<RenameLocation?> ResolveRenameLocationAsync(ILanguageTextModel model,
        LanguagePosition position, CancellationToken cancellationToken)
    {
        var snapshot = LanguageModelSnapshot.Capture(model);
        using var response = await RequestAsync(snapshot, position, 4, cancellationToken).ConfigureAwait(false);
        if (response is null || ReadLocations(response).Count == 0) return null;
        var text = new LanguageTextSnapshot(snapshot.GetValue());
        var offset = text.OffsetAt(position);
        var begin = offset;
        while (begin > 0 && IsIdentifier(text.Text[begin - 1])) begin--;
        var end = offset;
        while (end < text.Text.Length && IsIdentifier(text.Text[end])) end++;
        return end > begin ? new RenameLocation(text.Range(begin, end), text.Text[begin..end]) : null;
    }
    public async ValueTask<WorkspaceEdit?> ProvideRenameEditsAsync(ILanguageTextModel model,
        LanguagePosition position, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(newName) || !(char.IsLetter(newName[0]) || newName[0] == '_') ||
            !newName.All(IsIdentifier)) return null;
        // libclang references are deliberately bounded to the active translation unit/file.
        // Never try a textual search across unsaved headers or an unindexed workspace.
        var references = await ProvideReferencesAsync(model, position, new ReferenceContext(true), cancellationToken).ConfigureAwait(false);
        if (references is null || references.Count == 0) return null;
        return new WorkspaceEdit(references.Where(r => r.Uri == model.Uri)
            .Select(r => new WorkspaceTextEdit(r.Uri, r.Range, newName))
            .Distinct().ToArray());
    }
    private static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value == '_';
    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ILanguageTextModel model,
        LanguageRange range, CodeActionContext context, CancellationToken cancellationToken)
    {
        if (context.Only is not null && !"quickfix".StartsWith(context.Only, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<CodeAction>();
        var diagnostics = await ProvideDiagnosticsAsync(model, cancellationToken).ConfigureAwait(false);
        if (diagnostics is null) return Array.Empty<CodeAction>();
        var snapshot = new LanguageTextSnapshot(model.GetValue());
        var actions = new List<CodeAction>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!diagnostic.Message.Contains("expected ';' after", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Message.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Range.EndLineNumber < range.StartLineNumber ||
                diagnostic.Range.StartLineNumber > range.EndLineNumber) continue;
            var offset = snapshot.OffsetAt(new LanguagePosition(diagnostic.Range.EndLineNumber, diagnostic.Range.EndColumn));
            var left = offset;
            while (left > 0 && char.IsWhiteSpace(snapshot.Text[left - 1])) --left;
            if (left == 0 || snapshot.Text[left - 1] != '}') continue;
            if (offset < snapshot.Text.Length && snapshot.Text[offset] == ';') continue;
            actions.Add(new CodeAction("Insert missing semicolon", "quickfix",
                new[] { new LanguageTextEdit(snapshot.Range(left, left), ";") }, IsPreferred: true));
        }
        return actions;
    }
    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideReferencesAsync(ILanguageTextModel model,
        LanguagePosition position, ReferenceContext context, CancellationToken cancellationToken)
    {
        var snapshot = LanguageModelSnapshot.Capture(model);
        using var result = await RequestAsync(snapshot, position, 4, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        var locations = ReadLocations(result);
        if (context.IncludeDeclaration) return locations;
        var definitions = await ProvideDefinitionAsync(snapshot, position, cancellationToken).ConfigureAwait(false);
        return definitions is null ? locations : locations.Where(location => !definitions.Contains(location)).ToArray();
    }
}

/// <summary>
/// Reads the unambiguous 'arguments' form of compile_commands.json. A shell 'command' string is
/// deliberately not executed or guessed. Hosts can supply an argument provider for that case.
/// Compiler plugins/response files are not accepted from a compilation database.
/// </summary>
public static class CppCompilationDatabase
{
    public static IReadOnlyList<string>? ReadArguments(string databasePath, string sourceFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath); ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        var full = Path.GetFullPath(databasePath);
        if (Directory.Exists(full)) full = Path.Combine(full, "compile_commands.json");
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("C++ compilation database does not exist.", full);
        if (info.Length > 32 * 1024 * 1024) throw new InvalidDataException("C++ compilation database exceeds 32 MiB.");
        using var document = JsonDocument.Parse(File.ReadAllText(full));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("C++ compilation database root must be an array.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        sourceFile = Path.GetFullPath(sourceFile);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("directory", out var directoryValue) || directoryValue.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("file", out var fileValue) || fileValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("C++ compilation entries require string directory and file fields.");
            var directory = Path.GetFullPath(directoryValue.GetString()!, Path.GetDirectoryName(full)!);
            var file = Path.GetFullPath(fileValue.GetString()!, directory);
            if (!string.Equals(file, sourceFile, comparison)) continue;
            if (!entry.TryGetProperty("arguments", out var raw))
                throw new InvalidDataException("This compile_commands entry only contains a shell command. Supply CompilerArgumentsProvider; shell commands are never executed.");
            if (raw.ValueKind != JsonValueKind.Array || raw.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("C++ compiler arguments must be a string array.");
            var values = raw.EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (values.Length == 0) throw new InvalidDataException("C++ compiler argument list is empty.");
            var compiler = Path.GetFileNameWithoutExtension(values[0]);
            if (compiler is "ccache" or "sccache" or "distcc" or "gomacc")
                throw new InvalidDataException("Compiler launcher entries require CompilerArgumentsProvider.");
            var result = new List<string> { "-working-directory=" + directory };
            for (var i = 1; i < values.Length; ++i)
            {
                var value = values[i];
                if (value == "-c" || value == "/c") continue;
                if (value is "-o" or "-MF" or "-MT" or "-MQ" or "--output") { ++i; continue; }
                if (value is "-MD" or "-MMD" or "-MP") continue;
                if (value.StartsWith("@", StringComparison.Ordinal) || value.StartsWith("-Xclang", StringComparison.Ordinal) ||
                    value.StartsWith("-load", StringComparison.Ordinal) || value.StartsWith("-plugin", StringComparison.Ordinal) ||
                    value.StartsWith("-fplugin", StringComparison.Ordinal) || value.StartsWith("-fpass-plugin", StringComparison.Ordinal) ||
                    value.StartsWith("/clang:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Compiler plugins, -Xclang and response files require explicit trusted host arguments.");
                if (!value.StartsWith('-') && string.Equals(Path.GetFullPath(value, directory), sourceFile, comparison)) continue;
                result.Add(value);
            }
            return result;
        }
        return null;
    }
}
