using System.Security.Cryptography;
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

    public static int Main(string[] args)
    {
        Console.WriteLine("[RendererGolden] Main entered");
        try
        {
            var output = GetArg(args, "--output") ?? Path.Combine(Path.GetTempPath(), "monaconative-render-golden.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

            Console.WriteLine("[RendererGolden] First RenderSnapshot starting");
            var first = RenderSnapshot();
            Console.WriteLine("[RendererGolden] Second RenderSnapshot starting");
            var second = RenderSnapshot();
            if (first.InkPixels <= 100)
                throw new InvalidOperationException("DirectWrite probe produced an unexpectedly empty image.");
            if (!string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal) || first.InkPixels != second.InkPixels)
                throw new InvalidOperationException("Software-rendered DirectWrite probe was not deterministic within one process.");

            File.WriteAllText(output, JsonSerializer.Serialize(first, new JsonSerializerOptions { WriteIndented = true }));

            var baseline = GetArg(args, "--baseline");
            if (!string.IsNullOrWhiteSpace(baseline) && File.Exists(baseline))
            {
                var expected = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(baseline))
                    ?? throw new InvalidDataException("Renderer baseline is invalid.");
                if (!string.Equals(expected.Sha256, first.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Renderer golden mismatch. Expected {expected.Sha256}, got {first.Sha256}.");
            }

            Console.WriteLine($"Renderer probe PASS: {first.InkPixels} ink pixels, SHA-256 {first.Sha256}");
            Console.WriteLine($"Snapshot: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Renderer probe FAIL: {ex}");
            return 1;
        }
    }

    private static Snapshot RenderSnapshot()
    {
        const int width = 720;
        const int height = 240;
        const float dpi = 96;
        Console.WriteLine("[RendererGolden] Creating software CanvasDevice");
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Console.WriteLine("[RendererGolden] CanvasDevice created");
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
        long ink = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            // A white BGRA/RGBA pixel has all RGB channels near 255. Channel order does not
            // matter for this luminance-independent "ink" predicate.
            if (pixels[i] < 245 || pixels[i + 1] < 245 || pixels[i + 2] < 245)
                ++ink;
        }
        var sha = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        return new Snapshot("monaconative-render-golden-v1", width, height, dpi, device.ForceSoftwareRenderer, ink, sha, Corpus);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; ++i)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
