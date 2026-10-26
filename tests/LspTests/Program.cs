using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MonacoNative.Language;
using MonacoNative.Lsp;

static class Test
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }
}

sealed class FakeModel : ILanguageTextModel
{
    public Uri Uri { get; init; } = new("file:///test.cs");
    public string LanguageId { get; init; } = "csharp";
    public ulong VersionId => 1;
    public int LineCount => 1;
    public string GetValue() => "Console.WriteLine(value);";
    public string GetLineContent(int lineNumber) => GetValue();
    public int GetLineMaxColumn(int lineNumber) => GetValue().Length + 1;
}

sealed class MutableSyncDocument : ILspSynchronizedDocument
{
    private string _text;
    private ulong _version = 1;
    public MutableSyncDocument(string text, string languageId) { _text = text; LanguageId = languageId; }
    public string Text => _text;
    public string LanguageId { get; private set; }
    public ulong VersionId => _version;
    public event EventHandler? Changed;
    public void SetText(string text) { _text = text; _version++; Changed?.Invoke(this, EventArgs.Empty); }
    public void SetLanguage(string languageId) { LanguageId = languageId; _version++; Changed?.Invoke(this, EventArgs.Empty); }
}

sealed class FakeServerState
{
    public TaskCompletionSource<JsonElement> DidOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<JsonElement> DidChange { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<JsonElement> DidClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<JsonElement> CodeActionParams { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

class Program
{
    static async Task Main()
    {
        await FramingRoundTrip();
        IncrementalSync();
        SnippetCompletion();
        await TcpLifecycleAndProviderBridge();
        TransportSurface();
        Console.WriteLine("MonacoNative LSP tests passed.");
    }

    static async Task FramingRoundTrip()
    {
        var json = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"test\",\"params\":{\"text\":\"你好😀\"}}");
        await using var stream = new MemoryStream();
        await LspMessageFraming.WriteMessageAsync(stream, json);
        stream.Position = 0;
        var read = await LspMessageFraming.ReadMessageAsync(stream);
        Test.True(read is not null, "framed message should be readable");
        Test.Equal(Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(read!), "framing must preserve UTF-8 payload");
    }

    static void IncrementalSync()
    {
        var change = LspTextSynchronization.ComputeMinimalChange("line1\r\nhello 😀\nend", "line1\r\nhello brave 😀\nend");
        Test.True(change is not null, "insert should yield a change");
        Test.Equal(new LspTextPosition(1, 6), change!.Value.Start, "incremental start position");
        Test.Equal(new LspTextPosition(1, 6), change.Value.End, "incremental end position");
        Test.Equal("brave ", change.Value.Text, "incremental inserted text");

        var emoji = LspTextSynchronization.ComputeMinimalChange("x😀y", "x😁y");
        Test.True(emoji is not null, "emoji replacement should yield a change");
        Test.Equal(new LspTextPosition(0, 1), emoji!.Value.Start, "surrogate-safe start");
        Test.Equal(new LspTextPosition(0, 3), emoji.Value.End, "surrogate-safe end");
        Test.Equal("😁", emoji.Value.Text, "surrogate-safe replacement");

        var crlf = LspTextSynchronization.ComputeMinimalChange("a\r\nb", "a\nb");
        Test.True(crlf is not null, "CRLF edit should yield a change");
        Test.Equal(new LspTextPosition(0, 1), crlf!.Value.Start, "CRLF-safe start");
        Test.Equal(new LspTextPosition(1, 0), crlf.Value.End, "CRLF-safe end must not split the CRLF pair");
        Test.Equal("\n", crlf.Value.Text, "CRLF-safe replacement");
        Test.True(LspTextSynchronization.ComputeMinimalChange("same", "same") is null, "equal text should not produce didChange");
    }

    static void SnippetCompletion()
    {
        Test.Equal("Console.WriteLine(value);", LspSnippetText.Expand("Console.WriteLine(${1:value});$0"), "snippet placeholders should expand to plain text");
        Test.Equal("red", LspSnippetText.Expand("${1|red,green,blue|}"), "snippet choices select first option");
        Test.Equal("$", LspSnippetText.Expand("\\$"), "escaped dollars remain literal");
        var snippet = LspSnippetText.ExpandWithCursor("Console.WriteLine(${1:value});$0");
        Test.Equal("Console.WriteLine(value);", snippet.Text, "snippet default text");
        Test.Equal<int?>("Console.WriteLine(".Length, snippet.CaretOffset, "initial tabstop must determine caret location");
        Test.Equal<int?>(3, LspSnippetText.ExpandWithCursor("abc$0").CaretOffset, "final tabstop without numbered fields");
    }

    static async Task TcpLifecycleAndProviderBridge()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverState = new FakeServerState();
        var server = RunFakeServer(listener, serverState);

        await using var client = new LspClient(new TcpLspTransport("127.0.0.1", port));
        var diagnosticsTcs = new TaskCompletionSource<LspDiagnosticsEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticsPublished += (_, e) => diagnosticsTcs.TrySetResult(e);
        await client.InitializeAsync(new LspInitializeOptions { ClientName = "MonacoNative.Tests", ClientVersion = "9" });
        Test.Equal(LspClientState.Initialized, client.State, "client lifecycle after initialize");
        Test.Equal(LspTextDocumentSyncKind.Incremental, client.ServerCapabilities.TextDocumentSync, "sync capability");
        Test.True(client.ServerCapabilities.TextDocumentOpenClose, "open/close capability");
        Test.True(client.ServerCapabilities.Hover, "hover capability");
        Test.True(client.ServerCapabilities.SemanticTokens, "semantic tokens capability");
        Test.Equal(".", client.ServerCapabilities.CompletionTriggerCharacters.Single(), "completion trigger");

        var languages = new MonacoLanguageService(registerBuiltins: false);
        using var adapter = new LspLanguageAdapter(client, languages, LanguageSelector.ForLanguage("csharp"));
        using var fallback = languages.RegisterCompletionItemProvider(LanguageSelector.ForLanguage("csharp"), new WordCompletionProvider(new[] { "public" }));
        var completion = await languages.ProvideCompletionItemsAsync(new FakeModel(), new LanguagePosition(1, 8), new CompletionContext());
        Test.True(completion.Suggestions.Count == 1, "LSP completion should suppress offline fallback suggestions");
        Test.Equal("Console.WriteLine(value);", completion.Suggestions.Single().InsertText, "LSP snippet completion must be readable");
        var item = completion.Suggestions.Single();
        Test.Equal("Console.WriteLine(${1:value});$0", item.SnippetText!, "raw snippet must survive adapter");
        Test.True(item.Preselect && item.Deprecated, "preselect and deprecated tags");
        Test.Equal("System.Console", item.LabelDescription!, "labelDetails preserved");
        Test.Equal(8, item.InsertRange!.Value.EndColumn, "default insert range");
        Test.Equal(12, item.ReplaceRange!.Value.EndColumn, "separate default replace range");
        Test.True(item.Resolve is not null, "resolve provider attached");
        var resolved = await item.Resolve!(CancellationToken.None);
        Test.Equal("resolved documentation", resolved.Documentation!, "lazy completion documentation");
        Test.Equal("Console.WriteLine(${1:value});$0", resolved.SnippetText!, "resolver does not discard original snippet");
        Test.Equal(1, resolved.AdditionalTextEdits!.Count, "resolved import edits retained");
        var hover = await languages.ProvideHoverAsync(new FakeModel(), new LanguagePosition(1, 8));
        Test.True(hover is not null && hover.Contents.Any(x => x.Value.Contains("hover-from-server", StringComparison.Ordinal)), "hover should cross LSP/provider boundary");

        var diagnostics = await diagnosticsTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Test.Equal(new Uri("file:///test.cs"), diagnostics.Uri, "diagnostic URI");
        Test.Equal(1, diagnostics.Diagnostics.Count, "diagnostic count");
        Test.Equal(DiagnosticSeverity.Warning, diagnostics.Diagnostics[0].Severity, "diagnostic severity");

        var actions = await languages.ProvideCodeActionsAsync(new FakeModel(), new LanguageRange(1, 1, 1, 8), new CodeActionContext());
        Test.Equal(1, actions.Count, "code action should cross LSP/provider boundary");
        Test.Equal("Fix warning", actions[0].Title, "code action title");
        Test.True(actions[0].WorkspaceEdit?.Edits.Count == 1, "code action workspace edit should be preserved");
        var codeActionParams = await serverState.CodeActionParams.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var sentDiagnostic = codeActionParams.GetProperty("context").GetProperty("diagnostics")[0];
        Test.Equal("warning-from-server", sentDiagnostic.GetProperty("message").GetString(), "code action must round-trip diagnostics");
        Test.Equal("fake.fix", sentDiagnostic.GetProperty("data").GetProperty("fixId").GetString(), "diagnostic data must survive code-action round trip");

        var syncDocument = new MutableSyncDocument("alpha\r\nhello 😀\r\nend", "csharp");
        await using (var session = new LspDocumentSession(client, syncDocument, new Uri("file:///sync.cs")))
        {
            await session.OpenAsync();
            var didOpen = await serverState.DidOpen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Test.Equal("file:///sync.cs", didOpen.GetProperty("textDocument").GetProperty("uri").GetString(), "didOpen URI");
            syncDocument.SetText("alpha\r\nhello brave 😀\r\nend");
            await session.FlushAsync();
            var didChange = await serverState.DidChange.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var contentChange = didChange.GetProperty("contentChanges")[0];
            var changeRange = contentChange.GetProperty("range");
            Test.Equal(1, changeRange.GetProperty("start").GetProperty("line").GetInt32(), "didChange start line");
            Test.Equal(6, changeRange.GetProperty("start").GetProperty("character").GetInt32(), "didChange start character");
            Test.Equal("brave ", contentChange.GetProperty("text").GetString(), "didChange must be incremental, not a full-document resend");
        }
        await serverState.DidClose.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await client.ShutdownAsync();
        Test.Equal(LspClientState.Stopped, client.State, "client lifecycle after shutdown");
        await server.WaitAsync(TimeSpan.FromSeconds(3));
    }

    static async Task RunFakeServer(TcpListener listener, FakeServerState state)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var running = true;
            while (running)
            {
                var bytes = await LspMessageFraming.ReadMessageAsync(stream);
                if (bytes is null) break;
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var requestId) ? requestId : (long?)null;
                switch (method)
                {
                    case "initialize":
                        Test.True(id is not null, "initialize must be a request");
                        Test.True(root.GetProperty("params").GetProperty("capabilities").GetProperty("textDocument")
                            .GetProperty("completion").GetProperty("completionItem").GetProperty("snippetSupport").GetBoolean(),
                            "official JSON server needs snippet support in client capabilities");
                        await Send(stream, new
                        {
                            jsonrpc = "2.0",
                            id,
                            result = new
                            {
                                serverInfo = new { name = "fake-lsp", version = "1.0" },
                                capabilities = new
                                {
                                    textDocumentSync = new { openClose = true, change = 2 },
                                    completionProvider = new { triggerCharacters = new[] { "." }, resolveProvider = true },
                                    hoverProvider = true,
                                    signatureHelpProvider = new { triggerCharacters = new[] { "(" }, retriggerCharacters = new[] { "," } },
                                    definitionProvider = true,
                                    referencesProvider = true,
                                    renameProvider = new { prepareProvider = true },
                                    documentFormattingProvider = true,
                                    documentRangeFormattingProvider = true,
                                    inlayHintProvider = true,
                                    codeActionProvider = true,
                                    semanticTokensProvider = new
                                    {
                                        legend = new { tokenTypes = new[] { "class", "variable" }, tokenModifiers = new[] { "declaration" } },
                                        full = true,
                                    },
                                },
                            },
                        });
                        break;
                    case "initialized":
                        await Send(stream, new
                        {
                            jsonrpc = "2.0",
                            method = "textDocument/publishDiagnostics",
                            @params = new
                            {
                                uri = "file:///test.cs",
                                version = 1,
                                diagnostics = new[]
                                {
                                    new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 7 } }, severity = 2, message = "warning-from-server", source = "fake", data = new { fixId = "fake.fix" } },
                                },
                            },
                        });
                        break;
                    case "textDocument/codeAction":
                        Test.True(id is not null, "codeAction must be a request");
                        if (root.TryGetProperty("params", out var codeActionParams)) state.CodeActionParams.TrySetResult(codeActionParams.Clone());
                        await Send(stream, new
                        {
                            jsonrpc = "2.0",
                            id,
                            result = new[]
                            {
                                new
                                {
                                    title = "Fix warning",
                                    kind = "quickfix",
                                    isPreferred = true,
                                    edit = new
                                    {
                                        changes = new Dictionary<string, object[]>
                                        {
                                            ["file:///test.cs"] = new object[]
                                            {
                                                new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 7 } }, newText = "Console" },
                                            },
                                        },
                                    },
                                },
                            },
                        });
                        break;
                    case "textDocument/didOpen":
                        if (root.TryGetProperty("params", out var openParams)) state.DidOpen.TrySetResult(openParams.Clone());
                        break;
                    case "textDocument/didChange":
                        if (root.TryGetProperty("params", out var changeParams)) state.DidChange.TrySetResult(changeParams.Clone());
                        break;
                    case "textDocument/didClose":
                        if (root.TryGetProperty("params", out var closeParams)) state.DidClose.TrySetResult(closeParams.Clone());
                        break;
                    case "textDocument/completion":
                        Test.True(id is not null, "completion must be a request");
                        await Send(stream, new
                        {
                            jsonrpc = "2.0", id,
                            result = new
                            {
                                isIncomplete = false,
                                itemDefaults = new
                                {
                                    insertTextFormat = 2,
                                    data = new { ticket = "completion-42" },
                                    editRange = new
                                    {
                                        insert = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 7 } },
                                        replace = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 11 } },
                                    },
                                },
                                items = new[] { new
                                {
                                    label = "Console.WriteLine", kind = 3,
                                    textEditText = "Console.WriteLine(${1:value});$0", preselect = true, tags = new[] { 1 },
                                    labelDetails = new { detail = "(string value)", description = "System.Console" },
                                } },
                            },
                        });
                        break;
                    case "completionItem/resolve":
                        Test.True(id is not null, "resolve must be a request");
                        Test.Equal("completion-42", root.GetProperty("params").GetProperty("data").GetProperty("ticket").GetString(), "itemDefaults.data round trip");
                        await Send(stream, new
                        {
                            jsonrpc = "2.0", id,
                            result = new
                            {
                                documentation = new { kind = "markdown", value = "resolved documentation" },
                                additionalTextEdits = new[] { new
                                {
                                    range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
                                    newText = "using System;\n",
                                } },
                            },
                        });
                        break;
                    case "textDocument/hover":
                        Test.True(id is not null, "hover must be a request");
                        await Send(stream, new { jsonrpc = "2.0", id, result = new { contents = new { kind = "markdown", value = "**hover-from-server**" } } });
                        break;
                    case "shutdown":
                        Test.True(id is not null, "shutdown must be a request");
                        await Send(stream, new { jsonrpc = "2.0", id, result = (object?)null });
                        break;
                    case "exit":
                        running = false;
                        break;
                }
            }
        }
        finally { listener.Stop(); }
    }

    static async Task Send(Stream stream, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await LspMessageFraming.WriteMessageAsync(stream, bytes);
    }

    static void TransportSurface()
    {
        var pipe = new NamedPipeLspTransport("monaconative-lsp-test");
        Test.True(pipe.Description.Contains("pipe:", StringComparison.Ordinal), "named-pipe transport surface");
        var stdio = new StdioLspTransport("language-server", new[] { "--stdio" });
        Test.True(stdio.Description.Contains("stdio:", StringComparison.Ordinal), "stdio transport surface");
    }
}
