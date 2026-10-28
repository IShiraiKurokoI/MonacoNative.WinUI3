using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;

namespace MonacoNative.RendererGolden.Tests;

internal static class Program
{
    private sealed record Snapshot(
        string Schema,
        int Width,
        int Height,
        float Dpi,
        bool SoftwareRenderer,
        long InkPixels,
        string Sha256,
        IReadOnlyList<string> Corpus);

    private static readonly string[] Corpus =
    {
        "const answer = 42; // MonacoNative",
        "中文かなカナ 한글 e\u0301 👍🏽 👩‍💻",
        "שלום world مرحبا 123",
        "A → B    tabs/spaces    {}[]()",
    };

    private sealed record RenderResult(Snapshot Metadata, byte[] Pixels);

    public static int Main(string[] args)
    {
        Console.WriteLine("[RendererGolden] Main entered");
        try
        {
            var output = GetArg(args, "--output") ?? Path.Combine(Path.GetTempPath(), "monaconative-render-golden.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

            var dpiArgument = GetArg(args, "--dpi") ?? "96";
            if (!float.TryParse(dpiArgument, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) ||
                dpi is not (96f or 120f or 144f or 192f))
                throw new ArgumentException("--dpi must be 96, 120, 144, or 192.");
            // Reuse one software device so both snapshots have the same DirectWrite
            // font/cache state. A warm-up render resolves lazy font fallbacks before
            // the two independent render targets are compared pixel-for-pixel.
            Console.WriteLine("[RendererGolden] Creating software CanvasDevice");
            using var device = new CanvasDevice(forceSoftwareRenderer: true);
            Console.WriteLine($"[RendererGolden] Warming up DirectWrite at {dpi} DPI");
            _ = RenderSnapshot(device, dpi);
            Console.WriteLine($"[RendererGolden] First RenderSnapshot at {dpi} DPI");
            var first = RenderSnapshot(device, dpi);
            Console.WriteLine($"[RendererGolden] Second RenderSnapshot at {dpi} DPI");
            var second = RenderSnapshot(device, dpi);
            if (first.Metadata.InkPixels <= 100)
                throw new InvalidOperationException("DirectWrite probe produced an unexpectedly empty image.");
            if (!string.Equals(first.Metadata.Sha256, second.Metadata.Sha256, StringComparison.Ordinal) || first.Metadata.InkPixels != second.Metadata.InkPixels)
                throw new InvalidOperationException($"Software-rendered DirectWrite probe was not deterministic within one process. " +
                    $"First: {first.Metadata.Sha256} ({first.Metadata.InkPixels} ink pixels); " +
                    $"second: {second.Metadata.Sha256} ({second.Metadata.InkPixels} ink pixels).");

            File.WriteAllText(output, JsonSerializer.Serialize(first.Metadata, new JsonSerializerOptions { WriteIndented = true }));
            var png = GetArg(args, "--png") ?? Path.ChangeExtension(output, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
            SavePng(png, first.Pixels, first.Metadata.Width, first.Metadata.Height);

            var baseline = GetArg(args, "--baseline");
            if (!string.IsNullOrWhiteSpace(baseline))
            {
                // A specified but absent baseline is a failure, never a silent fallback to
                // the two-renders-in-one-process smoke test.
                if (!File.Exists(baseline))
                    throw new FileNotFoundException("Approved Windows renderer baseline is missing", baseline);
                var expected = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(baseline))
                    ?? throw new InvalidDataException("Renderer baseline is invalid.");
                if (expected.Schema != first.Metadata.Schema || expected.Width != first.Metadata.Width ||
                    expected.Height != first.Metadata.Height || expected.Dpi != first.Metadata.Dpi ||
                    expected.SoftwareRenderer != first.Metadata.SoftwareRenderer ||
                    expected.InkPixels != first.Metadata.InkPixels ||
                    !expected.Corpus.SequenceEqual(first.Metadata.Corpus) ||
                    !string.Equals(expected.Sha256, first.Metadata.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Renderer golden mismatch. Expected {expected.Sha256}, got {first.Metadata.Sha256}.");
            }

            Console.WriteLine($"Renderer probe PASS: {first.Metadata.InkPixels} ink pixels, SHA-256 {first.Metadata.Sha256}");
            Console.WriteLine($"Snapshot: {output}; review image: {png}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Renderer probe FAIL: {ex}");
            return 1;
        }
    }

    private static RenderResult RenderSnapshot(CanvasDevice device, float dpi)
    {
        // The render target dimensions are DIPs; GetPixelBytes returns physical
        // pixels. Never pass DIP dimensions to the PNG writer on scaled displays.
        const int width = 720;
        const int height = 240;
        var pixelWidth = checked((int)MathF.Round(width * dpi / 96f));
        var pixelHeight = checked((int)MathF.Round(height * dpi / 96f));
        using var target = new CanvasRenderTarget(device, width, height, dpi);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 18,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.White);
            // Deterministic selection and caret primitives; this is a Win2D/DirectWrite
            // canary, NOT a full MonacoEditor control screenshot.
            ds.FillRectangle(10, 58, 370, 43, Microsoft.UI.Colors.LightBlue);
            ds.DrawLine(410, 64, 410, 91, Microsoft.UI.Colors.DarkBlue, 2);
            var y = 12f;
            foreach (var text in Corpus)
            {
                using var layout = new CanvasTextLayout(device, text, format, width - 24, 42);
                if (text.StartsWith("שלום", StringComparison.Ordinal))
                    layout.Direction = CanvasTextDirection.RightToLeftThenTopToBottom;
                ds.DrawTextLayout(layout, 12, y, Microsoft.UI.Colors.Black);
                y += 52;
            }
        }

        var pixels = target.GetPixelBytes();
        if (pixels.Length != checked(pixelWidth * pixelHeight * 4))
            throw new InvalidDataException("Unexpected pixel count for render target DPI.");
        long ink = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            // A white BGRA/RGBA pixel has all RGB channels near 255. Channel order does not
            // matter for this luminance-independent "ink" predicate.
            if (pixels[i] < 245 || pixels[i + 1] < 245 || pixels[i + 2] < 245)
                ++ink;
        }
        var sha = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        return new RenderResult(new Snapshot("monaconative-render-golden-v3", pixelWidth, pixelHeight, dpi,
            device.ForceSoftwareRenderer, ink, sha, Corpus), pixels);
    }

    // Self-contained PNG writer for visual approval; no external imaging package or
    // platform-specific System.Drawing dependency is needed for the BGRA8 probe.
    private static void SavePng(string path, byte[] bgra, int width, int height)
    {
        if (bgra.Length != checked(width * height * 4))
            throw new InvalidDataException("Unexpected Win2D pixel buffer length.");
        using var stream = File.Create(path);
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 6; // RGBA, 8 bits per channel.
        WriteChunk(stream, "IHDR", header);

        var raw = new byte[checked(height * (1 + width * 4))];
        for (var y = 0; y < height; y++)
        {
            var dstRow = y * (1 + width * 4);
            raw[dstRow] = 0; // PNG filter: None.
            for (var x = 0; x < width; x++)
            {
                var src = (y * width + x) * 4;
                var dst = dstRow + 1 + x * 4;
                raw[dst] = bgra[src + 2];
                raw[dst + 1] = bgra[src + 1];
                raw[dst + 2] = bgra[src];
                raw[dst + 3] = bgra[src + 3];
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        stream.Write(bytes);
        stream.Write(name);
        stream.Write(payload);
        uint crc = 0xFFFFFFFF;
        foreach (var b in name.Concat(payload))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ~crc);
        stream.Write(bytes);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; ++i)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
