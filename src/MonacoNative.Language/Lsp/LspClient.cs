using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Lsp;

public sealed class LspClient : IAsyncDisposable
{
    private static readonly string[] SemanticTokenTypes =
    {
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter",
        "variable", "property", "enumMember", "event", "function", "method", "macro", "label",
        "comment", "string", "keyword", "number", "regexp", "operator", "decorator"
    };
    private static readonly string[] SemanticTokenModifiers =
    {
        "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary"
    };

    private readonly ILspTransport _transport;
    private JsonRpcConnection? _rpc;
    private int _disposeState;
    private readonly object _diagnosticsGate = new();
    private readonly Dictionary<Uri, LspDiagnosticsEventArgs> _diagnostics = new();
    private IReadOnlyList<LspWorkspaceFolder> _workspaceFolders = Array.Empty<LspWorkspaceFolder>();

    public LspClient(ILspTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public LspClientState State { get; private set; } = LspClientState.Created;
    public LspServerCapabilities ServerCapabilities { get; private set; } = new();
    public string TransportDescription => _transport.Description;
    public IReadOnlyDictionary<Uri, LspDiagnosticsEventArgs> DiagnosticsSnapshot
    {
        get { lock (_diagnosticsGate) return new Dictionary<Uri, LspDiagnosticsEventArgs>(_diagnostics); }
    }
    /// <summary>Host-supplied settings for workspace/configuration requests (null if not configured).</summary>
    public Func<Uri?, string?, CancellationToken, ValueTask<object?>>? ConfigurationProvider { get; set; }
    public event EventHandler<LspDiagnosticsEventArgs>? DiagnosticsPublished;
    public event EventHandler<LspLogMessageEventArgs>? LogMessageReceived;
    public event EventHandler<LspNotificationEventArgs>? NotificationReceived;
    public event EventHandler<Exception>? ConnectionFaulted;

    /// <summary>Optional handler for server-to-client requests not handled by the built-in safe defaults.</summary>
    public Func<string, JsonElement?, CancellationToken, ValueTask<JsonElement?>>? ServerRequestHandler { get; set; }

    public async ValueTask InitializeAsync(LspInitializeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (State != LspClientState.Created) throw new InvalidOperationException($"Cannot initialize LSP client from state {State}.");
        options ??= new LspInitializeOptions();
        _workspaceFolders = options.WorkspaceFolders.ToArray();
        State = LspClientState.Starting;
        try
        {
            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
            _rpc = new JsonRpcConnection(_transport.Input, _transport.Output)
            {
                NotificationHandler = HandleNotificationAsync,
                RequestHandler = HandleServerRequestAsync,
            };
            _rpc.ConnectionFaulted += Rpc_ConnectionFaulted;
            _rpc.Start();

            var initializeParams = new
            {
                processId = options.ProcessId,
                clientInfo = new { name = options.ClientName, version = options.ClientVersion },
                locale = options.Locale,
                rootUri = options.RootUri?.AbsoluteUri,
                initializationOptions = options.InitializationOptions,
                capabilities = CreateClientCapabilities(),
                workspaceFolders = options.WorkspaceFolders.Count == 0 ? null : options.WorkspaceFolders.Select(x => new { uri = x.Uri.AbsoluteUri, name = x.Name }).ToArray(),
            };
            var result = await _rpc.SendRequestAsync("initialize", initializeParams, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Language server returned an invalid initialize result.");
            ServerCapabilities = LspServerCapabilities.Parse(result.Value);
            await _rpc.SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            State = LspClientState.Initialized;
        }
        catch
        {
            State = LspClientState.Faulted;
            throw;
        }
    }

    public async ValueTask<JsonElement?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await _rpc!.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _rpc!.SendNotificationAsync(method, parameters, cancellationToken);
    }

    public async ValueTask ExecuteCommandAsync(string command, IReadOnlyList<JsonElement>? arguments = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        await RequestAsync("workspace/executeCommand", new { command, arguments = arguments?.ToArray() }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (State is LspClientState.Stopped or LspClientState.Disposed or LspClientState.Created) return;
        if (_rpc is null) return;
        State = LspClientState.ShuttingDown;
        try
        {
            try { await _rpc.SendRequestAsync("shutdown", null, cancellationToken).ConfigureAwait(false); }
            finally
            {
                try { await _rpc.SendNotificationAsync("exit", null, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            State = LspClientState.Stopped;
        }
        catch
        {
            State = LspClientState.Faulted;
            throw;
        }
    }

    private static object CreateClientCapabilities() => new
    {
        workspace = new
        {
            applyEdit = false,
            workspaceEdit = new { documentChanges = true },
            workspaceFolders = true,
            configuration = true,
        },
        textDocument = new
        {
            synchronization = new { dynamicRegistration = false, willSave = false, willSaveWaitUntil = false, didSave = false },
            completion = new
            {
                dynamicRegistration = false, contextSupport = true,
                completionItem = new
                {
                    snippetSupport = true, commitCharactersSupport = false, // TSF commit-character interception is not advertised.
                    deprecatedSupport = true,
                    preselectSupport = true, insertReplaceSupport = true, labelDetailsSupport = true,
                    documentationFormat = new[] { "markdown", "plaintext" },
                    tagSupport = new { valueSet = new[] { 1 } },
                    insertTextModeSupport = new { valueSet = new[] { 1, 2 } },
                    resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits", "command" } },
                },
                completionItemKind = new { valueSet = Enumerable.Range(1, 25).ToArray() },
                completionList = new { itemDefaults = new[] { "commitCharacters", "editRange", "insertTextFormat", "insertTextMode", "data" } },
            },
            hover = new { dynamicRegistration = false, contentFormat = new[] { "markdown", "plaintext" } },
            signatureHelp = new { dynamicRegistration = false, signatureInformation = new { documentationFormat = new[] { "markdown", "plaintext" }, parameterInformation = new { labelOffsetSupport = true }, activeParameterSupport = true } },
            definition = new { dynamicRegistration = false, linkSupport = true },
            references = new { dynamicRegistration = false },
            rename = new { dynamicRegistration = false, prepareSupport = true },
            formatting = new { dynamicRegistration = false },
            rangeFormatting = new { dynamicRegistration = false },
            codeAction = new { dynamicRegistration = false, codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll" } } } },
            publishDiagnostics = new { relatedInformation = true, versionSupport = true, tagSupport = new { valueSet = new[] { 1, 2 } } },
            inlayHint = new { dynamicRegistration = false },
            semanticTokens = new
            {
                dynamicRegistration = false,
                requests = new { range = false, full = true },
                tokenTypes = SemanticTokenTypes,
                tokenModifiers = SemanticTokenModifiers,
                formats = new[] { "relative" },
                overlappingTokenSupport = false,
                multilineTokenSupport = false,
            },
        },
        general = new { positionEncodings = new[] { "utf-16" } },
        window = new { workDoneProgress = false },
    };

    private ValueTask HandleNotificationAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (method == "textDocument/publishDiagnostics" && parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            var p = parameters.Value;
            if (p.TryGetProperty("uri", out var uriElement) && Uri.TryCreate(uriElement.GetString(), UriKind.Absolute, out var uri))
            {
                var diagnostics = p.TryGetProperty("diagnostics", out var items) ? LspConversions.ParseDiagnostics(items) : Array.Empty<LanguageDiagnostic>();
                var version = p.TryGetProperty("version", out var versionElement) && versionElement.TryGetInt32(out var v) ? v : (int?)null;
                var args = new LspDiagnosticsEventArgs(uri, diagnostics, version);
                lock (_diagnosticsGate)
                {
                    if (diagnostics.Count == 0) _diagnostics.Remove(uri);
                    else _diagnostics[uri] = args;
                }
                DiagnosticsPublished?.Invoke(this, args);
                return ValueTask.CompletedTask;
            }
        }
        if ((method == "window/logMessage" || method == "window/showMessage") && parameters.HasValue && parameters.Value.ValueKind == JsonValueKind.Object)
        {
            var messageParams = parameters.Value;
            var type = messageParams.TryGetProperty("type", out var typeElement) && typeElement.TryGetInt32(out var t) ? t : 3;
            var message = messageParams.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
            LogMessageReceived?.Invoke(this, new LspLogMessageEventArgs(type, message));
            return ValueTask.CompletedTask;
        }
        NotificationReceived?.Invoke(this, new LspNotificationEventArgs(method, parameters));
        return ValueTask.CompletedTask;
    }

    private async ValueTask<JsonElement?> HandleServerRequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "workspace/configuration":
            {
                if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object ||
                    !parameters.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return JsonSerializer.SerializeToElement(Array.Empty<object?>(), JsonOptions.Default);
                var values = new List<object?>();
                foreach (var item in items.EnumerateArray())
                {
                    Uri? scope = null;
                    if (item.TryGetProperty("scopeUri", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String)
                        Uri.TryCreate(scopeValue.GetString(), UriKind.Absolute, out scope);
                    var section = item.TryGetProperty("section", out var sectionValue) && sectionValue.ValueKind == JsonValueKind.String
                        ? sectionValue.GetString() : null;
                    values.Add(ConfigurationProvider is null ? null :
                        await ConfigurationProvider(scope, section, cancellationToken).ConfigureAwait(false));
                }
                return JsonSerializer.SerializeToElement(values, JsonOptions.Default);
            }
            case "workspace/workspaceFolders":
                return JsonSerializer.SerializeToElement(
                    _workspaceFolders.Select(x => new { uri = x.Uri.AbsoluteUri, name = x.Name }).ToArray(), JsonOptions.Default);
            case "client/registerCapability":
            case "client/unregisterCapability":
            case "window/showMessageRequest":
                return JsonSerializer.SerializeToElement<object?>(null, JsonOptions.Default);
        }
        if (ServerRequestHandler is not null)
            return await ServerRequestHandler(method, parameters, cancellationToken).ConfigureAwait(false);
        throw new JsonRpcException(-32601, $"Unsupported server request: {method}");
    }

    private void Rpc_ConnectionFaulted(object? sender, Exception e)
    {
        State = LspClientState.Faulted;
        ConnectionFaulted?.Invoke(this, e);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (State != LspClientState.Initialized) throw new InvalidOperationException($"LSP client is not initialized (state: {State}).");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        try
        {
            if (State == LspClientState.Initialized)
            {
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await ShutdownAsync(timeout.Token).ConfigureAwait(false); }
                catch { }
            }
            if (_rpc is not null)
            {
                _rpc.ConnectionFaulted -= Rpc_ConnectionFaulted;
                await _rpc.DisposeAsync().ConfigureAwait(false);
                _rpc = null;
            }
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally { State = LspClientState.Disposed; }
    }
}
