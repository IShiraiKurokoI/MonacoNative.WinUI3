using System.Threading.Channels;

namespace MonacoNative.Lsp;

public interface ILspSynchronizedDocument
{
    string Text { get; }
    string LanguageId { get; }
    ulong VersionId { get; }
    event EventHandler? Changed;
}

/// <summary>
/// Keeps an editor document synchronized with one LSP text document. Changes are serialized,
/// coalesced, and sent as full or minimal UTF-16 range changes according to server capability.
/// </summary>
public sealed class LspDocumentSession : IAsyncDisposable, IDisposable
{
    private sealed record Snapshot(string Text, int Version, string LanguageId);

    private readonly LspClient _client;
    private readonly ILspSynchronizedDocument _document;
    private readonly Channel<Snapshot> _changes = Channel.CreateUnbounded<Snapshot>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _worker;
    private string _lastText = string.Empty;
    private string _lastLanguageId = string.Empty;
    private int _opened;
    private int _disposed;

    public LspDocumentSession(LspClient client, ILspSynchronizedDocument document, Uri uri)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _document = document ?? throw new ArgumentNullException(nameof(document));
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri) throw new ArgumentException("LSP document URI must be absolute.", nameof(uri));
    }

    public Uri Uri { get; }
    public bool IsOpen => Volatile.Read(ref _opened) != 0;

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _opened, 1) != 0) return;
        if (_client.State != LspClientState.Initialized)
        {
            Volatile.Write(ref _opened, 0);
            throw new InvalidOperationException("LSP client must be initialized before opening a document session.");
        }

        _lastText = _document.Text;
        _lastLanguageId = _document.LanguageId;
        _document.Changed += Document_Changed;
        try
        {
            if (_client.ServerCapabilities.TextDocumentOpenClose)
            {
                await _client.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri = Uri.AbsoluteUri,
                        languageId = _lastLanguageId,
                        version = VersionToInt(_document.VersionId),
                        text = _lastText,
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
            _worker = Task.Run(() => ProcessChangesAsync(_disposeCts.Token));
        }
        catch
        {
            _document.Changed -= Document_Changed;
            Volatile.Write(ref _opened, 0);
            throw;
        }
    }

    private void Document_Changed(object? sender, EventArgs e)
    {
        if (!IsOpen || Volatile.Read(ref _disposed) != 0) return;
        _changes.Writer.TryWrite(new Snapshot(_document.Text, VersionToInt(_document.VersionId), _document.LanguageId));
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Snapshot? latest = null;
                while (_changes.Reader.TryRead(out var item)) latest = item;
                if (latest is null) continue;

                if (!string.Equals(latest.LanguageId, _lastLanguageId, StringComparison.Ordinal))
                {
                    if (_client.ServerCapabilities.TextDocumentOpenClose)
                    {
                        await _client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = Uri.AbsoluteUri } }, cancellationToken).ConfigureAwait(false);
                        await _client.NotifyAsync("textDocument/didOpen", new
                        {
                            textDocument = new { uri = Uri.AbsoluteUri, languageId = latest.LanguageId, version = latest.Version, text = latest.Text },
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    _lastLanguageId = latest.LanguageId;
                    _lastText = latest.Text;
                    continue;
                }

                switch (_client.ServerCapabilities.TextDocumentSync)
                {
                    case LspTextDocumentSyncKind.Full:
                        await _client.NotifyAsync("textDocument/didChange", new
                        {
                            textDocument = new { uri = Uri.AbsoluteUri, version = latest.Version },
                            contentChanges = new[] { new { text = latest.Text } },
                        }, cancellationToken).ConfigureAwait(false);
                        break;
                    case LspTextDocumentSyncKind.Incremental:
                    {
                        var change = LspTextSynchronization.ComputeMinimalChange(_lastText, latest.Text);
                        if (change is not null)
                        {
                            await _client.NotifyAsync("textDocument/didChange", new
                            {
                                textDocument = new { uri = Uri.AbsoluteUri, version = latest.Version },
                                contentChanges = new[]
                                {
                                    new { range = new { start = new { line = change.Value.Start.Line, character = change.Value.Start.Character }, end = new { line = change.Value.End.Line, character = change.Value.End.Character } }, text = change.Value.Text },
                                },
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }
                }
                _lastText = latest.Text;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static int VersionToInt(ulong value) => value > int.MaxValue ? int.MaxValue : (int)value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _document.Changed -= Document_Changed;
        var wasOpen = Interlocked.Exchange(ref _opened, 0) != 0;
        _changes.Writer.TryComplete();
        if (wasOpen && _client.State == LspClientState.Initialized && _client.ServerCapabilities.TextDocumentOpenClose)
            _ = _client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = Uri.AbsoluteUri } }).AsTask();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _document.Changed -= Document_Changed;
        var wasOpen = Interlocked.Exchange(ref _opened, 0) != 0;
        _changes.Writer.TryComplete();
        _disposeCts.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (wasOpen && _client.State == LspClientState.Initialized && _client.ServerCapabilities.TextDocumentOpenClose)
        {
            try { await _client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = Uri.AbsoluteUri } }).ConfigureAwait(false); }
            catch { }
        }
        _disposeCts.Dispose();
    }
}
