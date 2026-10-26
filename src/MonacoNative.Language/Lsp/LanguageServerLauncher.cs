using System.Text.Json;
using MonacoNative.Language;

namespace MonacoNative.Lsp;

/// <summary>Launch real upstream servers; never replace a missing server with fabricated semantics.</summary>
public static class LanguageServerLauncher
{
    public static async ValueTask<LspClient> StartAsync(
        ILspLanguagePackage package, Uri? workspaceRoot = null, string? executableOverride = null,
        IEnumerable<string>? argumentOverride = null, string? workingDirectory = null,
        LspInitializeOptions? initializeOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var profile = package.Server;
        workingDirectory ??= workspaceRoot?.IsFile == true ? workspaceRoot.LocalPath : Environment.CurrentDirectory;
        var launch = ResolveCommand(profile, executableOverride, argumentOverride, workingDirectory);
        var transport = new StdioLspTransport(launch.Executable, launch.Arguments, workingDirectory, profile.Environment);
        var client = new LspClient(transport);
        var settings = profile.Configuration is null ? default : JsonSerializer.SerializeToElement(profile.Configuration);
        client.ConfigurationProvider = (_, section, _) => ValueTask.FromResult<object?>(ReadConfiguration(settings, section));
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(profile.StartupTimeout);
            await client.InitializeAsync(initializeOptions ?? new LspInitializeOptions
            {
                RootUri = workspaceRoot, InitializationOptions = profile.InitializationOptions,
                WorkspaceFolders = workspaceRoot is null ? Array.Empty<LspWorkspaceFolder>() :
                    new[] { new LspWorkspaceFolder(workspaceRoot,
                        workspaceRoot.IsFile ? Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceRoot.LocalPath)) : "workspace") },
            }, startup.Token).ConfigureAwait(false);
            if (profile.Configuration is not null)
                await client.NotifyAsync("workspace/didChangeConfiguration", new { settings = profile.Configuration }, startup.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static object? ReadConfiguration(JsonElement settings, string? section)
    {
        if (settings.ValueKind != JsonValueKind.Object) return null;
        if (string.IsNullOrEmpty(section)) return settings.Clone();
        if (settings.TryGetProperty(section, out var direct)) return direct.Clone();
        var value = settings;
        foreach (var component in section.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(component, out var next)) return null;
            value = next;
        }
        return value.Clone();
    }

    /// <summary>Resolve npm binaries without cmd.exe/shell interpolation; supports local and global npm installs.</summary>
    public static (string Executable, IReadOnlyList<string> Arguments) ResolveCommand(LanguageServerProfile profile,
        string? executableOverride, IEnumerable<string>? argumentOverride, string workingDirectory)
    {
        var executable = executableOverride ?? profile.Executable;
        var arguments = (argumentOverride ?? profile.Arguments).ToArray();
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return ("dotnet", new[] { executable }.Concat(arguments).ToArray());
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
            return ("node", new[] { executable }.Concat(arguments).ToArray());
        if (profile.NpmPackage is not null && (executableOverride is null || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            var roots = new List<string>();
            if (Path.GetDirectoryName(executable) is { Length: > 0 } explicitDirectory) roots.Add(Path.GetFullPath(explicitDirectory));
            for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent) roots.Add(dir.FullName);
            roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('"')));
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var modules in new[] { Path.Combine(root, "node_modules"), Path.Combine(root, "..", "lib", "node_modules") })
                {
                    var packageRoot = Path.GetFullPath(Path.Combine(modules, profile.NpmPackage.Replace('/', Path.DirectorySeparatorChar)));
                    var manifest = Path.Combine(packageRoot, "package.json");
                    if (!File.Exists(manifest)) continue;
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (!json.RootElement.TryGetProperty("bin", out var bins)) continue;
                    string? entry = bins.ValueKind == JsonValueKind.String ? bins.GetString() :
                        bins.TryGetProperty(profile.Executable, out var bin) ? bin.GetString() : null;
                    if (entry is null) continue;
                    var script = Path.GetFullPath(Path.Combine(packageRoot, entry));
                    if (!script.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(script)) continue;
                    return ("node", new[] { script }.Concat(arguments).ToArray());
                }
            }
            if (OperatingSystem.IsWindows())
                throw new FileNotFoundException($"Cannot locate {profile.NpmPackage}. Install it locally or globally with npm, or configure the server's JavaScript entry file. Windows .cmd shims are not executed through a shell.");
        }
        return (executable, arguments);
    }
}
