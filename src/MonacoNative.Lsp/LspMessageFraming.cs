using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MonacoNative.Lsp;

/// <summary>Content-Length framing used by the Language Server Protocol over byte streams.</summary>
public static class LspMessageFraming
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxMessageBytes = 128 * 1024 * 1024;

    public static async ValueTask WriteMessageAsync(Stream output, ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (utf8Json.Length > MaxMessageBytes)
            throw new InvalidDataException($"LSP message exceeds {MaxMessageBytes} bytes.");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {utf8Json.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(utf8Json, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadMessageAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new List<byte>(256);
        var one = new byte[1];
        var matched = 0;
        while (true)
        {
            var read = await input.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (header.Count == 0) return null;
                throw new EndOfStreamException("Unexpected EOF while reading an LSP header.");
            }
            header.Add(one[0]);
            if (header.Count > MaxHeaderBytes)
                throw new InvalidDataException("LSP header is too large.");

            matched = (matched, one[0]) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
            if (matched == 4) break;
        }

        var headerText = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(header));
        int? contentLength = null;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                throw new InvalidDataException("Invalid LSP Content-Length header.");
            contentLength = parsed;
        }
        if (contentLength is null)
            throw new InvalidDataException("Missing LSP Content-Length header.");
        if (contentLength > MaxMessageBytes)
            throw new InvalidDataException($"LSP message exceeds {MaxMessageBytes} bytes.");

        var payload = new byte[contentLength.Value];
        await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
