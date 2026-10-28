using System.Security.Cryptography;
using System.Text;

namespace MonacoNative.Documents;

/// <summary>
/// A file-backed resource with strict decoding, BOM preservation and conflict detection.
/// Read before writing an existing file. A missing file reads as an empty new document.
/// Saving never normalizes line endings and commits through a sibling temporary file.
/// </summary>
public sealed class FileDocumentProvider : IEditorDocumentProvider
{
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly Encoding _fallbackEncoding;
    private readonly byte[] _newFilePreamble;
    private readonly bool _readOnly;
    private Encoding _encoding;
    private byte[] _preamble;
    private byte[]? _baselineHash;
    private bool _hasSnapshot;
    private bool _existedAtSnapshot;

    public FileDocumentProvider(string path, string? languageId = null,
        Encoding? encoding = null, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(FilePath)))
            throw new ArgumentException("A document must name a file.", nameof(path));
        Uri = new Uri(FilePath, UriKind.Absolute);
        DisplayName = Path.GetFileName(FilePath);
        LanguageId = languageId ?? EditorDocumentLanguages.InferFromPath(FilePath);
        _readOnly = isReadOnly;
        _fallbackEncoding = MakeStrict(encoding ?? new UTF8Encoding(false, true));
        _newFilePreamble = encoding?.GetPreamble() ?? [];
        _encoding = _fallbackEncoding;
        _preamble = _newFilePreamble;
    }

    public string FilePath { get; }
    public Uri Uri { get; }
    public string DisplayName { get; }
    public string? LanguageId { get; }
    public bool IsReadOnly => _readOnly || (GetAttributesOrNull() is { } attributes &&
        (attributes & FileAttributes.ReadOnly) != 0);

    /// <summary>The detected encoding, or the configured encoding before the first read.</summary>
    public Encoding Encoding => (Encoding)_encoding.Clone();
    public bool HasByteOrderMark => _preamble.Length != 0;

    public async Task<EditorDocumentContent> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attributes = GetAttributesOrNull();
            if (attributes is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _encoding = _fallbackEncoding;
                _preamble = _newFilePreamble;
                _baselineHash = null;
                _existedAtSnapshot = false;
                _hasSnapshot = true;
                return new EditorDocumentContent("", LanguageId);
            }
            EnsureFile(attributes.Value);
            var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var (encoding, preambleLength) = DetectEncoding(bytes);
            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            cancellationToken.ThrowIfCancellationRequested();
            _encoding = encoding;
            _preamble = bytes[..preambleLength];
            _baselineHash = SHA256.HashData(bytes);
            _existedAtSnapshot = true;
            _hasSnapshot = true;
            return new EditorDocumentContent(text, LanguageId);
        }
        finally { _ioGate.Release(); }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            if (IsReadOnly) throw new UnauthorizedAccessException($"'{DisplayName}' is read-only.");
            await VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);

            // Encode before touching the target. Invalid/unrepresentable text must leave it intact.
            var content = _encoding.GetBytes(text);
            var bytes = new byte[checked(_preamble.Length + content.Length)];
            _preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, _preamble.Length);
            temporaryPath = Path.Combine(Path.GetDirectoryName(FilePath)!,
                $".monaconative-{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Atomic replacement uses the temporary inode on Unix; retain executable and
            // permission bits instead of replacing a script with the process's default mode.
            if (_existedAtSnapshot && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(FilePath));

            // Recheck after preparing the output to catch changes made during a slow save.
            await VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_existedAtSnapshot)
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, FilePath, overwrite: false);
            temporaryPath = null;

            // Cancellation after the commit must not report a failed save: bytes are now durable.
            _baselineHash = SHA256.HashData(bytes);
            _existedAtSnapshot = true;
            _hasSnapshot = true;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _ioGate.Release();
        }
    }

    private async Task VerifyUnchangedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = GetAttributesOrNull();
        if (attributes is { } value) EnsureFile(value);
        if (!_hasSnapshot)
        {
            if (attributes is not null)
                throw Conflict("Read the file before overwriting its existing content.");
            // A new resource can be saved directly, without an unnecessary initial read.
            _existedAtSnapshot = false;
            _hasSnapshot = true;
        }
        if ((attributes is not null) != _existedAtSnapshot)
            throw Conflict("The file was created or deleted outside the editor. Reload before saving.");
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException($"'{DisplayName}' is read-only.");
        var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        if (_baselineHash is null || !CryptographicOperations.FixedTimeEquals(_baselineHash, SHA256.HashData(bytes)))
            throw Conflict("The file changed outside the editor. Reload or save to another file.");
    }

    private FileAttributes? GetAttributesOrNull()
    {
        try { return File.GetAttributes(FilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void EnsureFile(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException($"'{FilePath}' is a directory, not a document.");
    }

    private EditorDocumentConflictException Conflict(string message) => new(Uri, message);

    private (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        var span = bytes.AsSpan();
        // UTF-32 LE must be checked before UTF-16 LE (both begin FF FE).
        if (span.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return (new UTF32Encoding(false, true, true), 4);
        if (span.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return (new UTF32Encoding(true, true, true), 4);
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(true, true), 3);
        if (span.StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, true, true), 2);
        if (span.StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, true, true), 2);
        return (_fallbackEncoding, 0);
    }

    private static Encoding MakeStrict(Encoding encoding)
    {
        var result = (Encoding)encoding.Clone();
        result.EncoderFallback = EncoderFallback.ExceptionFallback;
        result.DecoderFallback = DecoderFallback.ExceptionFallback;
        return result;
    }
}
