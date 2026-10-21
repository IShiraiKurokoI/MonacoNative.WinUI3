using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;

namespace MonacoNative.Lsp;

public interface ILspTransport : IAsyncDisposable
{
    string Description { get; }
    Stream Input { get; }
    Stream Output { get; }
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public sealed class StreamLspTransport : ILspTransport
{
    private readonly bool _ownsStreams;
    public StreamLspTransport(Stream input, Stream output, bool ownsStreams = false, string description = "stream")
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        _ownsStreams = ownsStreams;
        Description = description;
    }
    public string Description { get; }
    public Stream Input { get; }
    public Stream Output { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_ownsStreams) return;
        await Output.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(Input, Output)) await Input.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => StopAsync();
}

public sealed class StdioLspTransport : ILspTransport
{
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _environment;
    private Process? _process;
    private Task<string>? _stderrTask;

    public StdioLspTransport(string fileName, IEnumerable<string>? arguments = null, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Language server executable is required.", nameof(fileName));
        _fileName = fileName;
        _arguments = (arguments ?? Array.Empty<string>()).ToArray();
        _workingDirectory = workingDirectory;
        _environment = environment;
    }

    public string Description => $"stdio:{_fileName}";
    public Stream Input => _process?.StandardOutput.BaseStream ?? throw new InvalidOperationException("Transport has not been started.");
    public Stream Output => _process?.StandardInput.BaseStream ?? throw new InvalidOperationException("Transport has not been started.");
    public event EventHandler<string>? StandardErrorReceived;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null) return ValueTask.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectory) ? Environment.CurrentDirectory : _workingDirectory,
        };
        foreach (var argument in _arguments) startInfo.ArgumentList.Add(argument);
        if (_environment is not null)
            foreach (var pair in _environment) startInfo.Environment[pair.Key] = pair.Value;
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start language server '{_fileName}'.");
        _stderrTask = DrainStderrAsync(_process);
        return ValueTask.CompletedTask;
    }

    private async Task<string> DrainStderrAsync(Process process)
    {
        var text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(text)) StandardErrorReceived?.Invoke(this, text);
        return text;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            if (_stderrTask is not null) await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            _stderrTask = null;
        }
    }

    public ValueTask DisposeAsync() => StopAsync();
}

public sealed class TcpLspTransport : ILspTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    public TcpLspTransport(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = port;
    }
    public string Description => $"tcp:{_host}:{_port}";
    public Stream Input => _stream ?? throw new InvalidOperationException("Transport has not been started.");
    public Stream Output => _stream ?? throw new InvalidOperationException("Transport has not been started.");
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null) return;
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var client = Interlocked.Exchange(ref _client, null);
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
    }
    public ValueTask DisposeAsync() => StopAsync();
}

public sealed class NamedPipeLspTransport : ILspTransport
{
    private readonly string _serverName;
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    public NamedPipeLspTransport(string pipeName, string serverName = ".")
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        _pipeName = pipeName;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "." : serverName;
    }
    public string Description => $"pipe:{_serverName}/{_pipeName}";
    public Stream Input => _pipe ?? throw new InvalidOperationException("Transport has not been started.");
    public Stream Output => _pipe ?? throw new InvalidOperationException("Transport has not been started.");
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_pipe is not null) return;
        var pipe = new NamedPipeClientStream(_serverName, _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _pipe = pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => StopAsync();
}
