using System.Text;
using MonacoNative.Documents;

namespace MonacoNative.Documents.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task Run(string name, Func<Task> action)
    {
        try
        {
            await action();
            ++_passed;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            ++_failed;
            Console.Error.WriteLine($"[FAIL] {name}: {ex}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Bytes(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("The encoded bytes changed unexpectedly.");
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static byte[] Encode(Encoding encoding, string text) =>
        [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    public static async Task<int> Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"monaconative-documents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await Run("memory provider has independent stable identities and exact initial content", async () =>
            {
                var first = new MemoryDocumentProvider("a\r\nb\nc\r", "sample.cs");
                var second = new MemoryDocumentProvider("a", "sample.cs");
                Equal(false, EditorDocumentUriComparer.Instance.Equals(first.Uri, second.Uri));
                Equal(first.Uri, first.Uri);
                Equal("a\r\nb\nc\r", (await first.ReadAsync()).Text);
                Equal("csharp", first.LanguageId);
                await first.WriteAsync("saved\r\n");
                Equal("saved\r\n", first.Text);
                Equal("a", second.Text);
            });

            await Run("memory provider honors explicit language and URI", async () =>
            {
                var uri = new Uri("custom://workspace/folder/my%20file.cs");
                var provider = new MemoryDocumentProvider("hello", "my file.cs", "plaintext", uri);
                Equal(uri, provider.Uri);
                Equal("plaintext", (await provider.ReadAsync()).LanguageId);
            });

            await Run("read-only memory rejects saves without mutating content", async () =>
            {
                var provider = new MemoryDocumentProvider("original", isReadOnly: true);
                await Throws<UnauthorizedAccessException>(() => provider.WriteAsync("changed"));
                Equal("original", (await provider.ReadAsync()).Text);
            });

            await Run("canceled memory reads and writes leave content unchanged", async () =>
            {
                var provider = new MemoryDocumentProvider("original");
                var canceled = new CancellationToken(true);
                await Throws<OperationCanceledException>(() => provider.ReadAsync(canceled));
                await Throws<OperationCanceledException>(() => provider.WriteAsync("changed", canceled));
                Equal("original", provider.Text);
            });

            await Run("relative memory URIs are rejected", () =>
                Throws<ArgumentException>(() =>
                {
                    _ = new MemoryDocumentProvider(uri: new Uri("relative.cs", UriKind.Relative));
                    return Task.CompletedTask;
                }));

            await Run("file identities normalize paths and infer languages", () =>
            {
                var a = new FileDocumentProvider(Path.Combine(directory, "child", "..", "sample.TS"));
                var b = new FileDocumentProvider(Path.Combine(directory, "sample.TS"));
                Equal(true, EditorDocumentUriComparer.Instance.Equals(a.Uri, b.Uri));
                Equal(EditorDocumentUriComparer.Instance.GetHashCode(a.Uri),
                    EditorDocumentUriComparer.Instance.GetHashCode(b.Uri));
                Equal("typescript", a.LanguageId);
                Equal("sample.TS", a.DisplayName);
                return Task.CompletedTask;
            });

            await Run("file identity honors platform casing rules", () =>
            {
                var a = new Uri(Path.Combine(directory, "CASE.cs"));
                var b = new Uri(Path.Combine(directory, "case.cs"));
                Equal(OperatingSystem.IsWindows(), EditorDocumentUriComparer.Instance.Equals(a, b));
                return Task.CompletedTask;
            });

            var encodings = new (string Name, Encoding Encoding)[]
            {
                ("utf8-no-bom", new UTF8Encoding(false, true)),
                ("utf8-bom", new UTF8Encoding(true, true)),
                ("utf16-le", new UnicodeEncoding(false, true, true)),
                ("utf16-be", new UnicodeEncoding(true, true, true)),
                ("utf32-le", new UTF32Encoding(false, true, true)),
                ("utf32-be", new UTF32Encoding(true, true, true))
            };
            foreach (var (name, encoding) in encodings)
            {
                await Run($"file preserves {name}, BOM, Unicode and mixed line endings", async () =>
                {
                    var path = Path.Combine(directory, $"{name}.txt");
                    const string original = "中文\r\nemoji 😀\nlast\r";
                    var originalBytes = Encode(encoding, original);
                    await File.WriteAllBytesAsync(path, originalBytes);
                    var provider = new FileDocumentProvider(path);
                    Equal(original, (await provider.ReadAsync()).Text);
                    Equal(encoding.CodePage, provider.Encoding.CodePage);
                    Equal(encoding.GetPreamble().Length > 0, provider.HasByteOrderMark);
                    await provider.WriteAsync(original);
                    Bytes(originalBytes, await File.ReadAllBytesAsync(path));
                    var updated = original + "added\r\n";
                    await provider.WriteAsync(updated);
                    Bytes(Encode(encoding, updated), await File.ReadAllBytesAsync(path));
                });
            }

            await Run("existing no-BOM file stays no-BOM with a BOM-configured fallback", async () =>
            {
                var path = Path.Combine(directory, "no-bom.txt");
                await File.WriteAllBytesAsync(path, "abc"u8.ToArray());
                var provider = new FileDocumentProvider(path, encoding: new UTF8Encoding(true, true));
                await provider.ReadAsync();
                await provider.WriteAsync("def");
                Bytes("def"u8.ToArray(), await File.ReadAllBytesAsync(path));
            });

            await Run("explicit legacy encoding round-trips without a BOM", async () =>
            {
                var path = Path.Combine(directory, "latin1.txt");
                await File.WriteAllBytesAsync(path, new byte[] { 0xE9, 0x0D, 0x0A });
                var provider = new FileDocumentProvider(path, encoding: Encoding.Latin1);
                Equal("é\r\n", (await provider.ReadAsync()).Text);
                await provider.WriteAsync("ö\r\n");
                Bytes([0xF6, 0x0D, 0x0A], await File.ReadAllBytesAsync(path));
                await Throws<EncoderFallbackException>(() => provider.WriteAsync("中文"));
                Bytes([0xF6, 0x0D, 0x0A], await File.ReadAllBytesAsync(path));
            });

            await Run("missing files read empty and new saves honor configured encoding", async () =>
            {
                var path = Path.Combine(directory, "new.cs");
                var encoding = new UnicodeEncoding(false, true, true);
                var provider = new FileDocumentProvider(path, encoding: encoding);
                Equal("", (await provider.ReadAsync()).Text);
                Equal(false, File.Exists(path));
                await provider.WriteAsync("class New {}\r\n");
                Bytes(Encode(encoding, "class New {}\r\n"), await File.ReadAllBytesAsync(path));
            });

            await Run("new resources may be saved without an initial read", async () =>
            {
                var path = Path.Combine(directory, "direct-new.txt");
                await new FileDocumentProvider(path).WriteAsync("new");
                Bytes("new"u8.ToArray(), await File.ReadAllBytesAsync(path));
            });

            await Run("existing files cannot be overwritten without an initial read", async () =>
            {
                var path = Path.Combine(directory, "unopened.txt");
                await File.WriteAllTextAsync(path, "original");
                await Throws<EditorDocumentConflictException>(() => new FileDocumentProvider(path).WriteAsync("changed"));
                Equal("original", await File.ReadAllTextAsync(path));
            });

            await Run("external edits are detected even when size and timestamp are unchanged", async () =>
            {
                var path = Path.Combine(directory, "conflict.txt");
                await File.WriteAllTextAsync(path, "first");
                var provider = new FileDocumentProvider(path);
                await provider.ReadAsync();
                var timestamp = File.GetLastWriteTimeUtc(path);
                await File.WriteAllTextAsync(path, "other");
                File.SetLastWriteTimeUtc(path, timestamp);
                await Throws<EditorDocumentConflictException>(() => provider.WriteAsync("my changes"));
                Equal("other", await File.ReadAllTextAsync(path));
                Equal("other", (await provider.ReadAsync()).Text);
                await provider.WriteAsync("reloaded");
                Equal("reloaded", await File.ReadAllTextAsync(path));
            });

            await Run("external creation and deletion require reload before saving", async () =>
            {
                var path = Path.Combine(directory, "appeared.txt");
                var provider = new FileDocumentProvider(path);
                await provider.ReadAsync();
                await File.WriteAllTextAsync(path, "external");
                await Throws<EditorDocumentConflictException>(() => provider.WriteAsync("overwrite"));
                Equal("external", await File.ReadAllTextAsync(path));
                await provider.ReadAsync();
                File.Delete(path);
                await Throws<EditorDocumentConflictException>(() => provider.WriteAsync("resurrect"));
                Equal(false, File.Exists(path));
            });

            await Run("read-only file providers reject writes", async () =>
            {
                var path = Path.Combine(directory, "configured-readonly.txt");
                await File.WriteAllTextAsync(path, "original");
                var provider = new FileDocumentProvider(path, isReadOnly: true);
                Equal("original", (await provider.ReadAsync()).Text);
                await Throws<UnauthorizedAccessException>(() => provider.WriteAsync("changed"));
                Equal("original", await File.ReadAllTextAsync(path));
            });

            await Run("file attribute becoming read-only is honored after opening", async () =>
            {
                var path = Path.Combine(directory, "attribute-readonly.txt");
                await File.WriteAllTextAsync(path, "original");
                var provider = new FileDocumentProvider(path);
                await provider.ReadAsync();
                var attributes = File.GetAttributes(path);
                try
                {
                    File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                    Equal(true, provider.IsReadOnly);
                    await Throws<UnauthorizedAccessException>(() => provider.WriteAsync("changed"));
                    Equal("original", await File.ReadAllTextAsync(path));
                }
                finally { File.SetAttributes(path, attributes); }
            });

            await Run("canceled save leaves disk bytes and snapshot usable", async () =>
            {
                var path = Path.Combine(directory, "canceled.txt");
                await File.WriteAllTextAsync(path, "original");
                var provider = new FileDocumentProvider(path);
                await provider.ReadAsync();
                await Throws<OperationCanceledException>(() => provider.WriteAsync("changed", new CancellationToken(true)));
                Equal("original", await File.ReadAllTextAsync(path));
                await provider.WriteAsync("retry");
                Equal("retry", await File.ReadAllTextAsync(path));
            });

            await Run("invalid UTF-8 is rejected without silently replacing characters", async () =>
            {
                var path = Path.Combine(directory, "invalid.txt");
                await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
                var provider = new FileDocumentProvider(path);
                await Throws<DecoderFallbackException>(() => provider.ReadAsync());
                await Throws<EditorDocumentConflictException>(() => provider.WriteAsync("overwrite"));
                Bytes([0xC3, 0x28], await File.ReadAllBytesAsync(path));
            });

            await Run("invalid UTF-16 editor text cannot destroy existing file bytes", async () =>
            {
                var path = Path.Combine(directory, "invalid-editor-text.txt");
                await File.WriteAllTextAsync(path, "original");
                var provider = new FileDocumentProvider(path);
                await provider.ReadAsync();
                await Throws<EncoderFallbackException>(() => provider.WriteAsync("\uD800"));
                Equal("original", await File.ReadAllTextAsync(path));
            });

            await Run("directories are never treated as writable document files", () =>
                Throws<IOException>(() => new FileDocumentProvider(directory).ReadAsync()));

            if (!OperatingSystem.IsWindows())
            {
                await Run("atomic save preserves Unix script permissions", async () =>
                {
                    if (OperatingSystem.IsWindows()) return;
                    var path = Path.Combine(directory, "executable.sh");
                    await File.WriteAllTextAsync(path, "#!/bin/sh\necho original\n");
                    var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
                    File.SetUnixFileMode(path, mode);
                    var provider = new FileDocumentProvider(path);
                    await provider.ReadAsync();
                    await provider.WriteAsync("#!/bin/sh\necho changed\n");
                    Equal(mode, File.GetUnixFileMode(path));
                });
            }

            await Run("completed and failed saves do not leave temporary files", () =>
            {
                Equal(0, Directory.GetFiles(directory, ".monaconative-*.tmp").Length);
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(directory, recursive: true); }

        Console.WriteLine($"Document provider tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
