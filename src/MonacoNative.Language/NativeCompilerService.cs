using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MonacoNative.Language;

/// <summary>An explicit in-memory file supplied to an in-process compiler. No project crawling.</summary>
public sealed record NativeLanguageDocument(Uri Uri, string Text);

/// <summary>
/// Shared ABI/lifetime/UTF-8 conversion for the native providers. JSON is an in-memory result
/// representation, NOT JSON-RPC: there is no socket, stdio transport, or child process.
/// Native calls run on a worker against immutable snapshots. Cancellation discards a completed
/// result; libclang's synchronous C API cannot be safely interrupted inside a call.
/// </summary>
public abstract class NativeCompilerService : ICompletionItemProvider, IHoverProvider,
    IDefinitionProvider, IDocumentDiagnosticsProvider, IDisposable
{
    private readonly bool _cpp;
    private readonly object _gate = new();
    private NativeCompilerBridge.CppHandle? _handle;
    private bool _disposed;
    private string? _lastError;
    protected NativeCompilerService(bool cpp) => _cpp = cpp;
    public string? LastError => Volatile.Read(ref _lastError);
    public abstract IReadOnlyList<string> TriggerCharacters { get; }
    protected abstract string LanguageSource { get; }
    protected abstract string DefaultExtension { get; }
    protected virtual string GetDocumentExtension(ILanguageTextModel model) => DefaultExtension;
    protected virtual string? CompilerLibraryPath => null;
    protected virtual IReadOnlyList<string> GetArguments(Uri uri) => Array.Empty<string>();
    protected virtual IReadOnlyList<NativeLanguageDocument> GetAdditionalDocuments() => Array.Empty<NativeLanguageDocument>();
    protected virtual bool SupportsModel(ILanguageTextModel model) => true;
    protected virtual bool SupportsOperation(ILanguageTextModel model, uint operation) => SupportsModel(model);
    protected virtual string? UnsupportedModelMessage(ILanguageTextModel model) => null;

    protected internal sealed class NativeResponse : IDisposable
    {
        private readonly Dictionary<string, SourceFile> _files;
        public JsonDocument Document { get; }
        public SourceFile Active { get; }
        internal NativeResponse(JsonDocument document, SourceFile[] files)
        {
            Document = document; Active = files[0];
            _files = files.ToDictionary(f => f.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }
        public SourceFile? FindSource(string path)
        {
            path = Path.GetFullPath(path);
            if (_files.TryGetValue(path, out var source)) return source;
            // Definitions may point into an actual include file. Read only that explicitly
            // returned file; never fall back to the active file's line map for another file.
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 32 * 1024 * 1024) return null;
                var text = File.ReadAllText(path);
                source = new SourceFile(new Uri(path), path, text);
                _files[path] = source; return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
        public void Dispose() => Document.Dispose();
    }

    protected internal sealed class SourceFile
    {
        public Uri Uri { get; }
        public string Path { get; }
        public string Text { get; }
        public byte[] Utf8 { get; }
        public LanguageTextSnapshot Snapshot { get; }
        private readonly int[] _byteStarts;
        private readonly int[] _charStarts;
        internal SourceFile(Uri uri, string path, string text)
        {
            Uri = uri; Path = path; Text = text; Snapshot = new(text); Utf8 = Encoding.UTF8.GetBytes(text);
            var bytes = new List<int> { 0 }; var chars = new List<int> { 0 };
            // A line map avoids rescanning a large file once for every returned diagnostic.
            for (int i = 0, b = 0; i < text.Length;)
            {
                var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                b += Encoding.UTF8.GetByteCount(text.AsSpan(i, length));
                var newline = text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'));
                i += length;
                if (newline) { bytes.Add(b); chars.Add(i); }
            }
            _byteStarts = bytes.ToArray(); _charStarts = chars.ToArray();
        }
        public int ByteOffset(LanguagePosition position)
        {
            var offset = Snapshot.OffsetAt(position);
            // A caret must not split a surrogate pair.
            if (offset > 0 && offset < Text.Length && char.IsLowSurrogate(Text[offset]) && char.IsHighSurrogate(Text[offset - 1])) --offset;
            return Encoding.UTF8.GetByteCount(Text.AsSpan(0, offset));
        }
        private int CharacterOffset(int offset)
        {
            offset = Math.Clamp(offset, 0, Utf8.Length);
            while (offset > 0 && offset < Utf8.Length && (Utf8[offset] & 0xc0) == 0x80) --offset;
            var row = Array.BinarySearch(_byteStarts, offset); if (row < 0) row = ~row - 1;
            return _charStarts[row] + Encoding.UTF8.GetCharCount(Utf8.AsSpan(_byteStarts[row], offset - _byteStarts[row]));
        }
        public LanguageRange Range(JsonElement value)
        {
            var start = value.GetProperty("start").GetInt32(); var end = value.GetProperty("end").GetInt32();
            return Snapshot.Range(CharacterOffset(start), CharacterOffset(Math.Max(start, end)));
        }
    }

    private SourceFile CaptureFile(Uri uri, string text, string? extension = null)
    {
        var path = uri.IsFile ? Path.GetFullPath(uri.LocalPath) : Path.Combine(Path.GetTempPath(), "MonacoNative",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..24] + (extension ?? DefaultExtension));
        return new(uri, path, text);
    }
    protected async ValueTask<NativeResponse?> RequestAsync(ILanguageTextModel model, LanguagePosition position,
        uint operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        model = LanguageModelSnapshot.Capture(model);
        if (!SupportsOperation(model, operation)) return null;
        var active = CaptureFile(model.Uri, model.GetValue(), GetDocumentExtension(model));
        var files = new List<SourceFile> { active };
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { active.Path };
        foreach (var additional in GetAdditionalDocuments())
        {
            var file = CaptureFile(additional.Uri, additional.Text);
            if (paths.Add(file.Path)) files.Add(file);
        }
        var offset = active.ByteOffset(position);
        var snapshots = files.ToArray();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var library = CompilerLibraryPath;
                var arguments = GetArguments(model.Uri).ToArray();
                string json;
                // Also protects create/dispose. Do not hold any editor/model lock here.
                lock (_gate)
                {
                    if (_disposed) return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    NativeCompilerBridge.EnsureAbi();
                    if (_cpp) _handle ??= NativeCompilerBridge.CreateCpp(library);
                    json = NativeCompilerBridge.Request(_handle, snapshots.Select(f => (f.Path, f.Utf8)).ToArray(), arguments, operation, offset);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var document = JsonDocument.Parse(json);
                Volatile.Write(ref _lastError, null);
                return new NativeResponse(document, snapshots);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NativeLanguageException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Volatile.Write(ref _lastError, ex.Message); return null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }
    protected static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    public async ValueTask<CompletionList?> ProvideCompletionItemsAsync(ILanguageTextModel model, LanguagePosition position,
        CompletionContext context, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(model, position, 0, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        var items = new List<CompletionItem>();
        foreach (var value in response.Document.RootElement.GetProperty("items").EnumerateArray())
        {
            var label = String(value, "label") ?? "";
            if (label.Length == 0) continue;
            var kind = Enum.TryParse<CompletionItemKind>(String(value, "kind"), out var parsed) ? parsed : CompletionItemKind.Text;
            var range = response.Active.Range(value);
            var snippet = String(value, "snippet");
            items.Add(new CompletionItem(label, kind, Detail: String(value, "detail"), Documentation: String(value, "documentation"),
                InsertText: String(value, "insertText") ?? label, SortText: String(value, "sort") ?? label, FilterText: String(value, "filterText") ?? label,
                Range: range, SnippetText: snippet, ReplaceRange: range,
                CommandId: String(value, "commandId") == "editor.action.triggerParameterHints" ? "editor.action.triggerParameterHints" : null,
                Deprecated: value.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True));
        }
        return new(items);
    }
    public async ValueTask<Hover?> ProvideHoverAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(model, position, 2, cancellationToken).ConfigureAwait(false);
        if (response is null || String(response.Document.RootElement, "hover") is not { Length: > 0 } content) return null;
        return new(new[] { new MarkdownString(content) }, response.Active.Range(response.Document.RootElement));
    }
    protected static IReadOnlyList<LanguageLocation> ReadLocations(NativeResponse response)
    {
        var locations = new List<LanguageLocation>();
        foreach (var value in response.Document.RootElement.GetProperty("locations").EnumerateArray())
            if (String(value, "file") is { } path && response.FindSource(path) is { } source)
                locations.Add(new(source.Uri, source.Range(value)));
        return locations;
    }
    public async ValueTask<IReadOnlyList<LanguageLocation>?> ProvideDefinitionAsync(ILanguageTextModel model, LanguagePosition position, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(model, position, 3, cancellationToken).ConfigureAwait(false);
        return response is null ? null : ReadLocations(response);
    }
    public async ValueTask<IReadOnlyList<LanguageDiagnostic>?> ProvideDiagnosticsAsync(ILanguageTextModel model, CancellationToken cancellationToken)
    {
        if (!SupportsModel(model))
            return UnsupportedModelMessage(model) is { } message ? new[] { new LanguageDiagnostic(new(1, 1, 1, 1), message, DiagnosticSeverity.Information, "NATIVE_SUBSET", LanguageSource) } : Array.Empty<LanguageDiagnostic>();
        using var response = await RequestAsync(model, new(1, 1), 1, cancellationToken).ConfigureAwait(false);
        if (response is null)
            return LastError is { } error ? new[] { new LanguageDiagnostic(new(1, 1, 1, 1), error, DiagnosticSeverity.Warning, "NATIVE_UNAVAILABLE", LanguageSource) } : Array.Empty<LanguageDiagnostic>();
        var diagnostics = new List<LanguageDiagnostic>();
        foreach (var value in response.Document.RootElement.GetProperty("diagnostics").EnumerateArray())
            diagnostics.Add(new(response.Active.Range(value), String(value, "message") ?? "Native language diagnostic",
                (DiagnosticSeverity)Math.Clamp(value.GetProperty("severity").GetInt32(), 1, 4), Source: LanguageSource));
        return diagnostics;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _handle?.Dispose(); _handle = null;
        }
        GC.SuppressFinalize(this);
    }
}

internal sealed class NativeLanguageException : Exception
{
    public NativeLanguageException(string message) : base(message) { }
}

internal static class NativeCompilerBridge
{
    private const string Library = "MonacoNative.Language.Native";
    static NativeCompilerBridge() => NativeLibrary.SetDllImportResolver(typeof(NativeCompilerBridge).Assembly, Resolve);
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        // Hosts/tests may explicitly point at the already-built core library. No download.
        if (Environment.GetEnvironmentVariable("MONACONATIVE_NATIVE_LIBRARY") is { Length: > 0 } configured)
            return NativeLibrary.Load(Path.GetFullPath(configured));
        var file = OperatingSystem.IsWindows() ? "MonacoNative.Core.Native.dll" : OperatingSystem.IsMacOS() ? "libMonacoNative.Core.Native.dylib" : "libMonacoNative.Core.Native.so";
        var local = Path.Combine(AppContext.BaseDirectory, file);
        return File.Exists(local) ? NativeLibrary.Load(local) : NativeLibrary.Load(file, assembly, DllImportSearchPath.SafeDirectories);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Document { public IntPtr Path, Text; public nuint Length; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint mn_language_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void mn_language_free(IntPtr value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mn_cpp_create([MarshalAs(UnmanagedType.LPUTF8Str)] string? path, out IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void mn_cpp_destroy(IntPtr session);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int mn_cpp_request(CppHandle session, [In] Document[] documents, uint count, [In] IntPtr[] arguments, uint argumentCount, uint operation, nuint offset, out IntPtr result);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int mn_typescript_request([In] Document[] documents, uint count, uint operation, nuint offset, out IntPtr result);
    internal sealed class CppHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal CppHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { mn_cpp_destroy(handle); return true; }
    }
    internal static void EnsureAbi()
    {
        if (mn_language_abi_version() != 1) throw new NativeLanguageException("Native language ABI mismatch. Rebuild and deploy MonacoNative.Core.Native with this managed version.");
    }
    private static string Take(IntPtr value)
    {
        if (value == IntPtr.Zero) return "{}";
        try { return Marshal.PtrToStringUTF8(value) ?? "{}"; }
        finally { mn_language_free(value); }
    }
    private static string Error(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? json : json;
        }
        catch (JsonException) { return json; }
    }
    internal static CppHandle CreateCpp(string? library)
    {
        var handle = mn_cpp_create(library, out var error); var message = Take(error);
        if (handle == IntPtr.Zero) throw new NativeLanguageException("In-process C++ service unavailable: " + Error(message) + " Deploy a matching-architecture libclang library, or set CppLanguageOptions.LibClangPath / MONACONATIVE_LIBCLANG. No clangd process is used.");
        return new(handle);
    }
    internal static string Request(CppHandle? session, (string Path, byte[] Text)[] source, string[] arguments, uint operation, int offset)
    {
        if (source.Length is 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(source));
        if (source.Any(s => s.Text.Length > 32 * 1024 * 1024) || source.Sum(s => (long)s.Text.Length) > 128 * 1024 * 1024) throw new NativeLanguageException("Native source snapshot limit exceeded.");
        var allocations = new List<IntPtr>(); var documents = new Document[source.Length]; var args = new IntPtr[arguments.Length];
        IntPtr AllocateString(string value)
        {
            if (value.Contains('\0')) throw new ArgumentException("Paths and compiler arguments cannot contain NUL.");
            var ptr = Marshal.StringToCoTaskMemUTF8(value); allocations.Add(ptr); return ptr;
        }
        try
        {
            for (var i = 0; i < source.Length; ++i)
            {
                var text = Marshal.AllocCoTaskMem(checked(source[i].Text.Length + 1)); allocations.Add(text);
                Marshal.Copy(source[i].Text, 0, text, source[i].Text.Length); Marshal.WriteByte(text, source[i].Text.Length, 0);
                documents[i] = new Document { Path = AllocateString(source[i].Path), Text = text, Length = (nuint)source[i].Text.Length };
            }
            for (var i = 0; i < args.Length; ++i) args[i] = AllocateString(arguments[i]);
            IntPtr output;
            var ok = session is null ? mn_typescript_request(documents, (uint)documents.Length, operation, (nuint)offset, out output) :
                mn_cpp_request(session, documents, (uint)documents.Length, args, (uint)args.Length, operation, (nuint)offset, out output);
            var json = Take(output);
            if (ok == 0) throw new NativeLanguageException(Error(json));
            return json;
        }
        finally { foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer); }
    }
}
