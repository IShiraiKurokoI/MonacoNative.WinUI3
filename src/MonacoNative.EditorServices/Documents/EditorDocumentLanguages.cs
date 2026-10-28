namespace MonacoNative.Documents;

/// <summary>Common Monaco language identifiers; a provider's explicit identifier takes precedence.</summary>
public static class EditorDocumentLanguages
{
    public static string? InferFromPath(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".cs" or ".csx" => "csharp",
        ".c" or ".h" => "c",
        ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" or ".hxx" or ".ixx" => "cpp",
        ".js" or ".mjs" or ".cjs" or ".jsx" => "javascript",
        ".ts" or ".mts" or ".cts" or ".tsx" => "typescript",
        ".json" or ".jsonc" => "json",
        ".html" or ".htm" => "html",
        ".css" => "css",
        ".scss" => "scss",
        ".less" => "less",
        ".xml" or ".xaml" or ".svg" or ".csproj" or ".props" or ".targets" => "xml",
        ".md" or ".markdown" => "markdown",
        ".py" or ".pyw" => "python",
        ".sh" or ".bash" => "shell",
        ".ps1" or ".psm1" or ".psd1" => "powershell",
        ".sql" => "sql",
        ".yaml" or ".yml" => "yaml",
        ".toml" => "toml",
        ".ini" or ".conf" or ".properties" => "ini",
        ".go" => "go",
        ".rs" => "rust",
        ".java" => "java",
        ".kt" or ".kts" => "kotlin",
        ".rb" => "ruby",
        ".php" => "php",
        ".bat" or ".cmd" => "bat",
        ".txt" or ".log" => "plaintext",
        _ => null
    };
}
