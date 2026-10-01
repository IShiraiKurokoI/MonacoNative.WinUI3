using Microsoft.UI.Xaml;
using MonacoNative.Core;
using MonacoNative.Language;
using MonacoNative.Lsp;

namespace MonacoNative.Renderer.WinUI3;

public sealed class LspEditorBinding : IAsyncDisposable, IDisposable
{
    private sealed class MonacoDocumentSource : ILspSynchronizedDocument, IDisposable
    {
        private readonly MonacoTextModel _model;
        public MonacoDocumentSource(MonacoTextModel model)
        {
            _model = model;
            _model.Changed += Model_Changed;
        }
        public string Text => _model.Text;
        public string LanguageId => _model.LanguageId;
        public ulong VersionId => _model.VersionId;
        public event EventHandler? Changed;
        private void Model_Changed(object? sender, MonacoModelChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => _model.Changed -= Model_Changed;
    }

    private readonly LspLanguageAdapter _adapter;
    private readonly LspDocumentSession _session;
    private readonly MonacoDocumentSource _source;
    private int _disposed;

    private LspEditorBinding(LspLanguageAdapter adapter, LspDocumentSession session, MonacoDocumentSource source)
    { _adapter = adapter; _session = session; _source = source; }

    public static async ValueTask<LspEditorBinding> AttachAsync(
        LspClient client,
        MonacoLanguageService languageService,
        MonacoTextModel model,
        Uri documentUri,
        LanguageSelector selector,
        CancellationToken cancellationToken = default)
    {
        var source = new MonacoDocumentSource(model);
        var adapter = new LspLanguageAdapter(client, languageService, selector);
        var session = new LspDocumentSession(client, source, documentUri);
        try
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new LspEditorBinding(adapter, session, source);
        }
        catch
        {
            adapter.Dispose();
            session.Dispose();
            source.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _adapter.Dispose();
        _session.Dispose();
        _source.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _adapter.Dispose();
        await _session.DisposeAsync().ConfigureAwait(false);
        _source.Dispose();
    }
}

public sealed partial class MonacoEditor
{
    private LspEditorBinding? _lspBinding;
    private CancellationTokenSource? _lspRefreshCts;
    private readonly DispatcherTimer _lspRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <summary>The active LSP-to-editor binding, if one has been attached.</summary>
    public LspEditorBinding? LanguageServerBinding => _lspBinding;

    /// <summary>
    /// Attaches an already initialized LSP client to this editor. The editor does not own the client;
    /// callers remain responsible for shutting down and disposing the client.
    /// </summary>
    public async Task AttachLanguageServerAsync(
        LspClient client,
        LanguageSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await DetachLanguageServerAsync().ConfigureAwait(true);
        selector ??= new LanguageSelector(Language: "*");
        _lspBinding = await LspEditorBinding.AttachAsync(client, _languages, _model, DocumentUri, selector, cancellationToken).ConfigureAwait(true);
        try
        {
            await RefreshSemanticTokensAsync(cancellationToken).ConfigureAwait(true);
            await RefreshInlayHintsAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            await RefreshContributionsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Individual capabilities are optional even when a server advertises broad support.
            // Explicit editor APIs still surface provider errors to the caller.
        }
    }

    public async Task DetachLanguageServerAsync()
    {
        _lspRefreshTimer.Stop();
        _lspRefreshCts?.Cancel();
        _lspRefreshCts?.Dispose();
        _lspRefreshCts = null;
        var binding = _lspBinding;
        _lspBinding = null;
        if (binding is not null) await binding.DisposeAsync().ConfigureAwait(true);
        ClearSemanticTokens();
        ClearLanguageInlayHints();
        _languages.ClearDiagnostics(DocumentUri);
    }

    private void ScheduleLspRefresh()
    {
        if (_lspBinding is null || _disposed || !_loaded) return;
        _lspRefreshTimer.Stop();
        _lspRefreshTimer.Start();
    }

    private void LspRefreshTimer_Tick(object? sender, object e)
    {
        _lspRefreshTimer.Stop();
        if (_lspBinding is null || _disposed || !_loaded) return;
        _lspRefreshCts?.Cancel();
        _lspRefreshCts?.Dispose();
        var source = new CancellationTokenSource();
        _lspRefreshCts = source;
        _ = RefreshLspFeaturesAfterEditAsync(source.Token);
    }

    private async Task RefreshLspFeaturesAfterEditAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshSemanticTokensAsync(cancellationToken).ConfigureAwait(true);
            await RefreshInlayHintsAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Background language intelligence refreshes must not tear down the editor.
        }
    }

    private void DisposeLspBinding()
    {
        _lspRefreshTimer.Stop();
        _lspRefreshTimer.Tick -= LspRefreshTimer_Tick;
        _lspRefreshCts?.Cancel();
        _lspRefreshCts?.Dispose();
        _lspRefreshCts = null;
        _lspBinding?.Dispose();
        _lspBinding = null;
    }
}
