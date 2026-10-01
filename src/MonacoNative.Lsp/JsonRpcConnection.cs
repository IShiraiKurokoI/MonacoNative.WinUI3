using System.Collections.Concurrent;
using System.Text.Json;

namespace MonacoNative.Lsp;

public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, JsonElement? data = null) : base(message)
    {
        Code = code;
        DataElement = data;
    }
    public int Code { get; }
    public JsonElement? DataElement { get; }
}

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private long _nextId;
    private Task? _readerTask;
    private int _disposed;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public Func<string, JsonElement?, CancellationToken, ValueTask>? NotificationHandler { get; set; }
    public Func<string, JsonElement?, CancellationToken, ValueTask<JsonElement?>>? RequestHandler { get; set; }
    public event EventHandler<Exception>? ConnectionFaulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _readerTask ??= Task.Run(() => ReadLoopAsync(_disposeCts.Token));
    }

    public async ValueTask<JsonElement?> SendRequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("JSON-RPC method is required.", nameof(method));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Start();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, tcs)) throw new InvalidOperationException("Duplicate JSON-RPC request id.");
        try
        {
            var envelope = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters is not null) envelope["params"] = parameters;
            await SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);

            try
            {
                return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                try { await SendNotificationAsync("$/cancelRequest", new { id }, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                throw;
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public ValueTask SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("JSON-RPC method is required.", nameof(method));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is not null) envelope["params"] = parameters;
        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    private async ValueTask SendEnvelopeAsync(object value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Default);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await LspMessageFraming.WriteMessageAsync(_output, payload, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = await LspMessageFraming.ReadMessageAsync(_input, cancellationToken).ConfigureAwait(false);
                if (payload is null) break;
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
                var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
                if (hasMethod)
                {
                    var method = methodElement.GetString()!;
                    JsonElement? parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : null;
                    if (hasId)
                        _ = HandleIncomingRequestAsync(idElement.Clone(), method, parameters, cancellationToken);
                    else if (NotificationHandler is not null)
                        await NotificationHandler(method, parameters, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!hasId || !TryReadNumericId(idElement, out var responseId) || !_pending.TryRemove(responseId, out var pending))
                    continue;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var c) ? c : -32603;
                    var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "JSON-RPC error" : "JSON-RPC error";
                    var data = error.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : (JsonElement?)null;
                    pending.TrySetException(new JsonRpcException(code, message, data));
                }
                else
                {
                    pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ConnectionFaulted?.Invoke(this, ex);
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
            _pending.Clear();
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var ex = new EndOfStreamException("The language server transport closed.");
                foreach (var pending in _pending.Values) pending.TrySetException(ex);
                _pending.Clear();
            }
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement? result = RequestHandler is null ? null : await RequestHandler(method, parameters, cancellationToken).ConfigureAwait(false);
            await SendResponseAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (JsonRpcException ex)
        {
            await SendErrorAsync(id, ex.Code, ex.Message, ex.DataElement, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(id, -32603, ex.Message, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ValueTask SendResponseAsync(JsonElement id, JsonElement? result, CancellationToken cancellationToken) =>
        SendEnvelopeAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonSerializer.Deserialize<object>(id.GetRawText(), JsonOptions.Default),
            ["result"] = result,
        }, cancellationToken);

    private ValueTask SendErrorAsync(JsonElement id, int code, string message, JsonElement? data, CancellationToken cancellationToken) =>
        SendEnvelopeAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonSerializer.Deserialize<object>(id.GetRawText(), JsonOptions.Default),
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message, ["data"] = data },
        }, cancellationToken);

    private static bool TryReadNumericId(JsonElement id, out long value)
    {
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out value)) return true;
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out value)) return true;
        value = 0;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeCts.Cancel();
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        _pending.Clear();
        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _disposeCts.Dispose();
        _writeGate.Dispose();
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
