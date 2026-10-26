using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using MonacoNative.Core;
using MonacoNative.Language;
using MonacoNative.Language.Json;
using MonacoNative.Language.Html;
using MonacoNative.Language.Css;
using MonacoNative.Language.TypeScript;
using MonacoNative.Language.CSharp;
using MonacoNative.Language.Cpp;
using Windows.UI;

namespace MonacoNative.Renderer.WinUI3;

public sealed partial class MonacoEditor
{
    private readonly MonacoLanguageService _languages;
    private readonly MonacoLanguageDocument _languageDocument;
    private readonly IncrementalTokenizationCache _languageTokens;
    private readonly List<ulong> _languageInlayHintIds = new();
    private IReadOnlyList<LanguageDiagnostic> _languageDiagnostics = Array.Empty<LanguageDiagnostic>();
    private LineRangeIndex<LanguageDiagnostic> _languageDiagnosticIndex = LineRangeIndex<LanguageDiagnostic>.Empty;
    private IReadOnlyDictionary<int, IReadOnlyList<SemanticToken>> _semanticTokensByLine = new Dictionary<int, IReadOnlyList<SemanticToken>>();

    private CancellationTokenSource? _nativeDiagnosticsCts;
    private Task? _nativeDiagnosticsTask;
    public Exception? LastNativeLanguageServiceError { get; private set; }
    public event EventHandler? NativeLanguageServiceFailed;

    private void CancelNativeDiagnostics()
    {
        var old = _nativeDiagnosticsCts;
        _nativeDiagnosticsCts = null;
        old?.Cancel();
        old?.Dispose();
    }

    private void ScheduleNativeDiagnostics()
    {
        CancelNativeDiagnostics();
        if (_disposed || !_loaded || _lspBinding is not null) return;
        if (!_languages.HasDiagnosticsProvider(_languageDocument))
        { _languages.ClearDiagnostics(DocumentUri); return; }
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ShutdownToken);
        _nativeDiagnosticsCts = source;
        _nativeDiagnosticsTask = _lifetime.Track(RefreshNativeDiagnosticsAfterDelayAsync(source, source.Token));
    }

    private async Task RefreshNativeDiagnosticsAfterDelayAsync(CancellationTokenSource source, CancellationToken token)
    {
        try
        {
            // Resume on the UI thread to capture the native text model, then analyze only the copy.
            await Task.Delay(250, token).ConfigureAwait(true);
            if (_disposed || !_loaded || _lspBinding is not null) return;
            var snapshot = LanguageModelSnapshot.Capture(_languageDocument);
            var diagnostics = await Task.Run(async () =>
                await _languages.ProvideDiagnosticsAsync(snapshot, token).ConfigureAwait(false), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (_disposed || !_loaded || _lspBinding is not null || !ReferenceEquals(_nativeDiagnosticsCts, source) || !snapshot.Matches(_languageDocument)) return;
            LastNativeLanguageServiceError = null;
            _languages.SetDiagnostics(snapshot.Uri, diagnostics, snapshot.VersionId > int.MaxValue ? int.MaxValue : (int)snapshot.VersionId);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_disposed || token.IsCancellationRequested) return;
            LastNativeLanguageServiceError = error;
            NativeLanguageServiceFailed?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine($"Native language diagnostics failed: {error}");
        }
        finally
        {
            if (ReferenceEquals(_nativeDiagnosticsCts, source))
            { _nativeDiagnosticsCts = null; source.Dispose(); }
        }
    }

    private Color _syntaxKeywordColor = Color.FromArgb(255, 86, 156, 214);
    private Color _syntaxStringColor = Color.FromArgb(255, 206, 145, 120);
    private Color _syntaxCommentColor = Color.FromArgb(255, 106, 153, 85);
    private Color _syntaxNumberColor = Color.FromArgb(255, 181, 206, 168);
    private Color _syntaxTypeColor = Color.FromArgb(255, 78, 201, 176);
    private Color _syntaxOperatorColor = Color.FromArgb(255, 212, 212, 212);
    private Color _syntaxInvalidColor = Color.FromArgb(255, 244, 71, 71);

    /// <summary>
    /// Installs built-in language modules. Each lives in a separate project and may also be
    /// used independently via MonacoLanguageService.Packages.Install(...).
    /// </summary>
    private void RegisterDefaultLanguagePackages()
    {
        _languages.Packages.Install(new JsonLanguagePackage());
        _languages.Packages.Install(new HtmlLanguagePackage());
        _languages.Packages.Install(new CssLanguagePackage());
        _languages.Packages.Install(new TypeScriptLanguagePackage());
        _languages.Packages.Install(new CSharpLanguagePackage());
        _languages.Packages.Install(new CppLanguagePackage());
    }

    /// <summary>Monaco-style language registry and provider surface.</summary>
    public MonacoLanguageService Languages => _languages;

    /// <summary>URI used by language selectors. Defaults to inmemory://model/1.</summary>
    public Uri DocumentUri
    {
        get => _languageDocument.Uri;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_languageDocument.Uri == value) return;
            CancelNativeDiagnostics();
            CancelAutomaticSuggestions();
            HideSuggestWidget();
            CancelSnippetSession();
            _languages.ClearDiagnostics(_languageDocument.Uri);
            _languageDocument.Uri = value;
            ScheduleFoldingRefresh();
            _languageDiagnostics = Array.Empty<LanguageDiagnostic>();
            _languageDiagnosticIndex = LineRangeIndex<LanguageDiagnostic>.Empty;
            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
            if (_loaded) Render(RenderDirty.Diagnostics);
            ScheduleNativeDiagnostics();
        }
    }

    public LanguageConfiguration? LanguageConfiguration => _languages.Languages.GetLanguageConfiguration(_model.LanguageId);
    public IReadOnlyList<LanguageDiagnostic> Diagnostics => _languageDiagnostics;
    public event EventHandler? DiagnosticsChanged;

    public ValueTask<CompletionList> ProvideCompletionItemsAsync(CompletionContext? context = null, CancellationToken cancellationToken = default) =>
        _languages.ProvideCompletionItemsAsync(_languageDocument, ToLanguagePosition(_active), context ?? new CompletionContext(), cancellationToken);

    public ValueTask<Hover?> ProvideHoverAsync(MonacoPosition position, CancellationToken cancellationToken = default) =>
        _languages.ProvideHoverAsync(_languageDocument, ToLanguagePosition(Normalize(position.LineNumber, position.Column)), cancellationToken);

    public async ValueTask<IReadOnlyList<LanguageLocation>> ProvideDefinitionAsync(MonacoPosition? position = null, CancellationToken cancellationToken = default) =>
        await _languages.ProvideDefinitionAsync(_languageDocument, ToLanguagePosition(position is null ? _active : Normalize(position.Value.LineNumber, position.Value.Column)), cancellationToken);

    public async Task GoToDefinitionAsync(CancellationToken cancellationToken = default)
    {
        var locations = await ProvideDefinitionAsync(cancellationToken: cancellationToken);
        var first = locations.FirstOrDefault();
        if (first is not null) NavigateToLocation(first);
    }

    public async Task FormatDocumentAsync(FormattingOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_isReadOnly) return;
        var snapshot = LanguageModelSnapshot.Capture(_languageDocument);
        var edits = await _languages.ProvideDocumentFormattingEditsAsync(snapshot, options ?? new FormattingOptions(), cancellationToken);
        if (_disposed || !snapshot.Matches(_languageDocument)) return;
        ApplyLanguageTextEdits(edits);
    }

    public async Task FormatSelectionAsync(FormattingOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_isReadOnly) return;
        var snapshot = LanguageModelSnapshot.Capture(_languageDocument);
        var selection = Selection;
        var edits = await _languages.ProvideDocumentRangeFormattingEditsAsync(snapshot, ToLanguageRange(selection), options ?? new FormattingOptions(), cancellationToken);
        if (_disposed || !snapshot.Matches(_languageDocument)) return;
        ApplyLanguageTextEdits(edits);
    }

    public async Task<SemanticTokens?> RefreshSemanticTokensAsync(CancellationToken cancellationToken = default)
    {
        var version = _model.VersionId;
        var result = await _languages.ProvideDocumentSemanticTokensAsync(_languageDocument, cancellationToken);
        if (_model.VersionId != version) return result;
        _semanticTokensByLine = result?.Tokens
            .GroupBy(token => token.LineNumber)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SemanticToken>)group.OrderBy(token => token.StartColumn).ToArray())
            ?? new Dictionary<int, IReadOnlyList<SemanticToken>>();
        _layoutCache?.Clear();
        Render(RenderDirty.Text);
        return result;
    }

    public void ClearSemanticTokens()
    {
        _semanticTokensByLine = new Dictionary<int, IReadOnlyList<SemanticToken>>();
        _layoutCache?.Clear();
        Render(RenderDirty.Text);
    }

    public ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(SignatureHelpContext? context = null, CancellationToken cancellationToken = default) =>
        _languages.ProvideSignatureHelpAsync(_languageDocument, ToLanguagePosition(_active), context ?? new SignatureHelpContext(), cancellationToken);

    public ValueTask<IReadOnlyList<CodeAction>> ProvideCodeActionsAsync(MonacoRange? range = null, CodeActionContext? context = null, CancellationToken cancellationToken = default)
    {
        var source = range ?? Selection;
        return _languages.ProvideCodeActionsAsync(_languageDocument, ToLanguageRange(source), context ?? new CodeActionContext(), cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InlayHint>> RefreshInlayHintsAsync(MonacoRange? range = null, CancellationToken cancellationToken = default)
    {
        ClearLanguageInlayHintsCore();
        var version = _model.VersionId;
        var source = range ?? new MonacoRange(1, 1, checked((int)_model.LineCount), _model.GetLineMaxColumn(checked((int)_model.LineCount)));
        var hints = await _languages.ProvideInlayHintsAsync(_languageDocument, ToLanguageRange(source), cancellationToken).ConfigureAwait(true);
        if (_model.VersionId != version) return hints;
        var order = 10_000;
        foreach (var hint in hints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = Normalize(hint.Position.LineNumber, hint.Position.Column);
            var label = (hint.PaddingLeft ? " " : string.Empty) + hint.Label + (hint.PaddingRight ? " " : string.Empty);
            var id = _view.AddInjectedText(position, label, MonacoInjectedTextAffinity.MoveAfterInsertion, order++);
            if (id != 0) _languageInlayHintIds.Add(id);
        }
        AfterProjectionChanged(false);
        return hints;
    }

    public void ClearLanguageInlayHints()
    {
        ClearLanguageInlayHintsCore();
        AfterProjectionChanged(false);
    }

    public async ValueTask ApplyCodeActionAsync(CodeAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.IsNullOrWhiteSpace(action.DisabledReason))
            throw new InvalidOperationException(action.DisabledReason);

        if (action.Edits is { Count: > 0 })
        {
            using var transaction = _model.BeginUndoTransaction();
            try
            {
                var edits = action.Edits
                    .Select(edit =>
                    {
                        var range = edit.Range.Normalize();
                        var startOffset = _model.GetOffsetAt(new MonacoPosition(range.StartLineNumber, range.StartColumn));
                        var endOffset = _model.GetOffsetAt(new MonacoPosition(range.EndLineNumber, range.EndColumn));
                        return (Start: startOffset, End: endOffset, Text: edit.Text ?? string.Empty);
                    })
                    .OrderByDescending(edit => edit.Start)
                    .ThenByDescending(edit => edit.End)
                    .ToArray();
                foreach (var edit in edits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _model.Replace(edit.Start, edit.End, edit.Text);
                }
                transaction.Complete();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        if (action.WorkspaceEdit is not null)
            await ApplyWorkspaceEditAsync(action.WorkspaceEdit, cancellationToken).ConfigureAwait(true);
        if (action.Command is not null)
            await action.Command(cancellationToken).ConfigureAwait(true);
        AfterProjectionChanged(true);
    }

    private void ClearLanguageInlayHintsCore()
    {
        foreach (var id in _languageInlayHintIds)
            _view.RemoveInjectedText(id);
        _languageInlayHintIds.Clear();
    }

    private void Languages_DiagnosticsChanged(object? sender, LanguageDiagnosticsChangedEventArgs e)
    {
        if (!Equals(e.Uri, DocumentUri)) return;
        var currentVersion = _model.VersionId > int.MaxValue ? int.MaxValue : (int)_model.VersionId;
        if (e.Version.HasValue && e.Version.Value < currentVersion) return;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !Equals(e.Uri, DocumentUri)) return;
            var latestVersion = _model.VersionId > int.MaxValue ? int.MaxValue : (int)_model.VersionId;
            if (e.Version.HasValue && e.Version.Value < latestVersion) return;
            _languageDiagnostics = e.Diagnostics.ToArray();
            _languageDiagnosticIndex = new LineRangeIndex<LanguageDiagnostic>(
                _languageDiagnostics,
                static diagnostic =>
                {
                    var range = diagnostic.Range.Normalize();
                    return (range.StartLineNumber, range.EndLineNumber);
                });
            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
            Render(RenderDirty.Diagnostics);
        });
    }

    private void Languages_TokenizationChanged(object? sender, string languageId)
    {
        if (!string.Equals(languageId, _model.LanguageId, StringComparison.OrdinalIgnoreCase)) return;
        _languageTokens.Clear();
        _bracketPairs.HandleTokenizationChanged(languageId);
        _layoutCache?.Clear();
        ScheduleDecorationRefresh(1, immediate: true);
        Render(RenderDirty.Text);
    }

    private void Languages_LanguageConfigurationChanged(object? sender, string languageId)
    {
        if (!string.Equals(languageId, _model.LanguageId, StringComparison.OrdinalIgnoreCase)) return;
        _bracketPairs.HandleLanguageConfigurationChanged(languageId);
        _layoutCache?.Clear();
        ScheduleDecorationRefresh(1, immediate: true);
        Render(RenderDirty.Text);
    }

    private void UpdateLanguagePalette(bool highContrast)
    {
        if (highContrast)
        {
            _syntaxKeywordColor = _foregroundColor;
            _syntaxStringColor = _foregroundColor;
            _syntaxCommentColor = _foregroundColor;
            _syntaxNumberColor = _foregroundColor;
            _syntaxTypeColor = _foregroundColor;
            _syntaxOperatorColor = _foregroundColor;
            _syntaxInvalidColor = _foregroundColor;
            return;
        }
        if (_lastTheme == Microsoft.UI.Xaml.ElementTheme.Light)
        {
            // VS Code Light Modern inherits Light+ token colors, not Dark+.
            _syntaxKeywordColor = Color.FromArgb(255, 0, 0, 255);
            _syntaxStringColor = Color.FromArgb(255, 163, 21, 21);
            _syntaxCommentColor = Color.FromArgb(255, 0, 128, 0);
            _syntaxNumberColor = Color.FromArgb(255, 9, 134, 88);
            _syntaxTypeColor = Color.FromArgb(255, 38, 127, 153);
            _syntaxOperatorColor = _foregroundColor;
            _syntaxInvalidColor = Color.FromArgb(255, 205, 49, 49);
            return;
        }
        _syntaxKeywordColor = Color.FromArgb(255, 86, 156, 214);
        _syntaxStringColor = Color.FromArgb(255, 206, 145, 120);
        _syntaxCommentColor = Color.FromArgb(255, 106, 153, 85);
        _syntaxNumberColor = Color.FromArgb(255, 181, 206, 168);
        _syntaxTypeColor = Color.FromArgb(255, 78, 201, 176);
        _syntaxOperatorColor = Color.FromArgb(255, 212, 212, 212);
        _syntaxInvalidColor = Color.FromArgb(255, 244, 71, 71);
    }

    private void ApplyLanguageTokenColors(CanvasTextLayout layout, MonacoViewLineInfo line)
    {
        using var methodSample = _methodProfiler.Measure("ApplyLanguageTokenColors");
        if (line.Text.Length == 0) return;
        var tokens = _languageTokens.GetLineTokens(line.ModelLineNumber).Tokens;
        if (tokens.Count > 0)
        {
            var tokenIndex = 0;
            Color? runColor = null;
            var runStart = 0;
            for (var visualIndex = 0; visualIndex <= line.Text.Length; visualIndex++)
            {
                Color? color = null;
                if (visualIndex < line.Text.Length)
                {
                    var before = _view.ViewToModel(new MonacoViewPosition(line.ViewLineNumber, visualIndex + 1));
                    var after = _view.ViewToModel(new MonacoViewPosition(line.ViewLineNumber, visualIndex + 2));
                    if (before.LineNumber == line.ModelLineNumber && after.LineNumber == line.ModelLineNumber && after.Column > before.Column)
                    {
                        var modelIndex = before.Column - 1;
                        while (tokenIndex < tokens.Count && tokens[tokenIndex].EndIndex <= modelIndex) tokenIndex++;
                        if (tokenIndex < tokens.Count && tokens[tokenIndex].StartIndex <= modelIndex && modelIndex < tokens[tokenIndex].EndIndex)
                            color = SyntaxColor(tokens[tokenIndex].Type);
                    }
                }

                if (Nullable.Equals(color, runColor)) continue;
                if (runColor.HasValue && visualIndex > runStart)
                    layout.SetColor(runStart, visualIndex - runStart, runColor.Value);
                runColor = color;
                runStart = visualIndex;
            }
        }
        ApplySemanticTokenColors(layout, line);
    }

    private void ApplySemanticTokenColors(CanvasTextLayout layout, MonacoViewLineInfo line)
    {
        using var methodSample = _methodProfiler.Measure("ApplySemanticTokenColors");
        if (!_semanticTokensByLine.TryGetValue(line.ModelLineNumber, out var tokens) || tokens.Count == 0 || line.Text.Length == 0)
            return;
        Color? runColor = null;
        var runStart = 0;
        var tokenIndex = 0;
        for (var visualIndex = 0; visualIndex <= line.Text.Length; visualIndex++)
        {
            Color? color = null;
            if (visualIndex < line.Text.Length)
            {
                var model = _view.ViewToModel(new MonacoViewPosition(line.ViewLineNumber, visualIndex + 1));
                if (model.LineNumber == line.ModelLineNumber)
                {
                    var column = model.Column;
                    while (tokenIndex < tokens.Count && tokens[tokenIndex].StartColumn + tokens[tokenIndex].Length <= column)
                        tokenIndex++;
                    if (tokenIndex < tokens.Count)
                    {
                        var token = tokens[tokenIndex];
                        if (token.StartColumn <= column && column < token.StartColumn + token.Length)
                            color = SemanticColor(token.TokenType);
                    }
                }
            }
            if (Nullable.Equals(color, runColor)) continue;
            if (runColor.HasValue && visualIndex > runStart)
                layout.SetColor(runStart, visualIndex - runStart, runColor.Value);
            runColor = color;
            runStart = visualIndex;
        }
    }

    private Color? SemanticColor(string tokenType) => tokenType switch
    {
        "keyword" => _syntaxKeywordColor,
        "string" or "regexp" => _syntaxStringColor,
        "comment" => _syntaxCommentColor,
        "number" => _syntaxNumberColor,
        "type" or "class" or "struct" or "interface" or "enum" or "typeParameter" => _syntaxTypeColor,
        "operator" => _syntaxOperatorColor,
        "namespace" or "function" or "method" or "property" or "event" => _lastHighContrast == true
            ? _foregroundColor : _lastTheme == Microsoft.UI.Xaml.ElementTheme.Light
                ? Color.FromArgb(255, 121, 94, 38) : Color.FromArgb(255, 220, 220, 170),
        "parameter" or "variable" or "enumMember" => _lastHighContrast == true
            ? _foregroundColor : _lastTheme == Microsoft.UI.Xaml.ElementTheme.Light
                ? Color.FromArgb(255, 0, 16, 128) : Color.FromArgb(255, 156, 220, 254),
        "macro" or "decorator" => _lastHighContrast == true
            ? _foregroundColor : _lastTheme == Microsoft.UI.Xaml.ElementTheme.Light
                ? Color.FromArgb(255, 175, 0, 219) : Color.FromArgb(255, 197, 134, 192),
        _ => null,
    };

    private Color? SyntaxColor(string tokenType)
    {
        if (string.IsNullOrEmpty(tokenType) || tokenType == "white" || tokenType == "identifier" || tokenType == "delimiter") return null;
        if (tokenType.StartsWith("comment", StringComparison.Ordinal)) return _syntaxCommentColor;
        if (tokenType.StartsWith("string", StringComparison.Ordinal)) return _syntaxStringColor;
        if (tokenType.StartsWith("keyword", StringComparison.Ordinal)) return _syntaxKeywordColor;
        if (tokenType.StartsWith("number", StringComparison.Ordinal)) return _syntaxNumberColor;
        if (tokenType.StartsWith("type", StringComparison.Ordinal)) return _syntaxTypeColor;
        if (tokenType.StartsWith("operator", StringComparison.Ordinal)) return _syntaxOperatorColor;
        if (tokenType.StartsWith("invalid", StringComparison.Ordinal)) return _syntaxInvalidColor;
        return null;
    }

    private void DrawLanguageDiagnostics(CanvasDrawingSession ds, int firstViewLine, int lastViewLine, double scrollTop, double scrollLeft)
    {
        using var methodSample = _methodProfiler.Measure("DrawLanguageDiagnostics");
        if (_languageDiagnosticIndex.Count == 0 || _layoutCache is null || firstViewLine > lastViewLine) return;

        var visibleModelLines = Enumerable.Range(firstViewLine, lastViewLine - firstViewLine + 1)
            .Select(viewLineNumber => _view.GetViewLine(viewLineNumber).ModelLineNumber)
            .Distinct()
            .ToArray();
        if (visibleModelLines.Length == 0) return;

        // Query only model lines that are actually visible. This matters for folded ranges: a
        // viewport can jump from line 20 to line 20,000, and hidden diagnostics must not turn
        // one frame into work proportional to the folded region.
        var byModelLine = new Dictionary<int, IReadOnlyList<LanguageDiagnostic>>();
        foreach (var modelLine in visibleModelLines)
        {
            var diagnostics = _languageDiagnosticIndex.Query(modelLine, modelLine);
            if (diagnostics.Count > 0)
                byModelLine[modelLine] = diagnostics;
        }
        if (byModelLine.Count == 0) return;

        for (var viewLineNumber = firstViewLine; viewLineNumber <= lastViewLine; viewLineNumber++)
        {
            var viewLine = _view.GetViewLine(viewLineNumber);
            var modelLine = viewLine.ModelLineNumber;
            if (!byModelLine.TryGetValue(modelLine, out var diagnostics))
                continue;

            var y = VerticalPadding + viewLine.VerticalOffset - scrollTop + _lineHeight - 2.0;
            var layout = _layoutCache.GetOrCreate(viewLineNumber, viewLine.Text, (float)_lineHeight, l =>
            {
                ApplyLanguageTokenColors(l, viewLine);
                ApplyBracketPairColors(l, viewLine);
            });
            foreach (var diagnostic in diagnostics)
            {
                var range = diagnostic.Range.Normalize();
                var maxColumn = _model.GetLineMaxColumn(modelLine);
                var startColumn = modelLine == range.StartLineNumber ? Math.Clamp(range.StartColumn, 1, maxColumn) : 1;
                var endColumn = modelLine == range.EndLineNumber ? Math.Clamp(range.EndColumn, startColumn, maxColumn) : maxColumn;
                var startModel = new MonacoPosition(modelLine, startColumn);
                var endModel = new MonacoPosition(modelLine, endColumn);
                var startView = _view.ModelToView(startModel);
                var endView = _view.ModelToView(endModel);
                if (viewLineNumber < startView.LineNumber || viewLineNumber > endView.LineNumber) continue;
                var startIndex = viewLineNumber == startView.LineNumber ? Math.Clamp(startView.Column - 1, 0, viewLine.Text.Length) : 0;
                var endIndex = viewLineNumber == endView.LineNumber ? Math.Clamp(endView.Column - 1, startIndex, viewLine.Text.Length) : viewLine.Text.Length;
                if (endIndex <= startIndex) endIndex = Math.Min(viewLine.Text.Length, startIndex + 1);
                var regions = TextRangeRegions(layout, viewLine.Text, startIndex, endIndex);
                if (regions.Length == 0)
                {
                    var x1 = GutterWidth + LeftPadding + CaretX(layout, viewLine.Text, startIndex) - scrollLeft;
                    var x2 = GutterWidth + LeftPadding + CaretX(layout, viewLine.Text, endIndex) - scrollLeft;
                    var left = Math.Min(x1, x2);
                    var right = Math.Max(left + 3.0, Math.Max(x1, x2));
                    DrawDiagnosticSquiggle(ds, (float)left, (float)right, (float)y, DiagnosticColor(diagnostic.Severity));
                    continue;
                }
                foreach (var region in regions)
                {
                    var bounds = region.LayoutBounds;
                    var x1 = GutterWidth + LeftPadding + bounds.X - scrollLeft;
                    var x2 = x1 + Math.Max(3.0, bounds.Width);
                    DrawDiagnosticSquiggle(ds, (float)x1, (float)x2, (float)y, DiagnosticColor(diagnostic.Severity));
                }
            }
        }
    }

    private static void DrawDiagnosticSquiggle(CanvasDrawingSession ds, float x1, float x2, float y, Color color)
    {
        const float step = 4f;
        var x = x1;
        var up = true;
        while (x < x2)
        {
            var next = Math.Min(x + step, x2);
            ds.DrawLine(x, y + (up ? 0 : 2), next, y + (up ? 2 : 0), color, 1.2f);
            up = !up;
            x = next;
        }
    }

    private static Color DiagnosticColor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Warning => Color.FromArgb(255, 205, 160, 0),
        DiagnosticSeverity.Information => Color.FromArgb(255, 55, 148, 255),
        DiagnosticSeverity.Hint => Color.FromArgb(255, 120, 120, 120),
        _ => Color.FromArgb(255, 244, 71, 71),
    };

    private static LanguagePosition ToLanguagePosition(MonacoPosition position) => new(position.LineNumber, position.Column);
    private static LanguageRange ToLanguageRange(MonacoRange range) => new(range.StartLineNumber, range.StartColumn, range.EndLineNumber, range.EndColumn);
}
