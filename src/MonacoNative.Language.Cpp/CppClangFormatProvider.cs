using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MonacoNative.Language;

namespace MonacoNative.Language.Cpp;

/// <summary>
/// Document and range formatting through LLVM clang-format. The compiler's libclang C API
/// does not expose LibFormat; this optional executable uses standard input/output and
/// never modifies a source file in place. A missing executable returns no edits.
/// </summary>
public sealed class CppClangFormatProvider : IDocumentFormattingEditProvider, IDocumentRangeFormattingEditProvider
{
    private readonly string? _executable;
    private readonly string? _toolchainRoot;

    public CppClangFormatProvider(string? executable = null, string? toolchainRoot = null)
    {
        _executable = executable;
        _toolchainRoot = toolchainRoot;
    }

    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentFormattingEditsAsync(
        ILanguageTextModel model, FormattingOptions options, CancellationToken cancellationToken) =>
        FormatAsync(model, null, cancellationToken);

    public ValueTask<IReadOnlyList<LanguageTextEdit>?> ProvideDocumentRangeFormattingEditsAsync(
        ILanguageTextModel model, LanguageRange range, FormattingOptions options, CancellationToken cancellationToken) =>
        FormatAsync(model, range, cancellationToken);

    private async ValueTask<IReadOnlyList<LanguageTextEdit>?> FormatAsync(
        ILanguageTextModel model, LanguageRange? range, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ResolveExecutable();
        if (executable is null) return null;
        var original = model.GetValue();
        var fileName = model.Uri.IsFile ? model.Uri.LocalPath : "source.cpp";
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        if (model.Uri.IsFile && Directory.Exists(Path.GetDirectoryName(fileName)))
            info.WorkingDirectory = Path.GetDirectoryName(fileName)!;
        info.ArgumentList.Add("--style=file");
        info.ArgumentList.Add("--fallback-style=Microsoft");
        info.ArgumentList.Add("--assume-filename=" + fileName);
        if (range is { } selection)
        {
            var start = Math.Clamp(selection.StartLineNumber, 1, model.LineCount);
            var end = Math.Clamp(selection.EndLineNumber, start, model.LineCount);
            // A range request formats the selected logical lines without modifying disk.
            info.ArgumentList.Add($"--lines={start}:{end}");
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception error) when (error is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // The optional formatter is not installed on this machine.
        }

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(original.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var formatted = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false); // Drain stderr to prevent deadlock.
            if (process.ExitCode != 0) return null;
            if (string.Equals(original, formatted, StringComparison.Ordinal))
                return Array.Empty<LanguageTextEdit>();

            // Return a single undoable editor edit; clang-format preserves the user's
            // other text verbatim when --lines is supplied.
            return new[] { new LanguageTextEdit(
                new LanguageRange(1, 1, model.LineCount, model.GetLineMaxColumn(model.LineCount)), formatted) };
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (IOException)
        {
            // A formatter which exits early may close stdin while it is still being
            // written. Do not apply partial output as an editor replacement.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return null;
        }
    }

    private string? ResolveExecutable()
    {
        var configured = _executable ?? Environment.GetEnvironmentVariable("MONACONATIVE_CLANG_FORMAT");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var root = _toolchainRoot ?? Environment.GetEnvironmentVariable("MONACONATIVE_LLVM_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "clang-format.exe" : "clang-format");
            if (File.Exists(candidate)) return candidate;
        }
        // PATH lookup is a convenience for developer machines; the editor never
        // downloads or installs a tool, and no command is derived from source text.
        var binary = OperatingSystem.IsWindows() ? "clang-format.exe" : "clang-format";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), binary);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
