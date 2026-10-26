namespace MonacoNative.Language;

/// <summary>
/// A separately deployable language contribution. The core owns only the editor/provider contracts;
/// concrete grammar definitions and native language engines live in language-specific assemblies.
/// </summary>
public interface ILanguagePackage
{
    string Id { get; }
    IReadOnlyList<string> LanguageIds { get; }
    LanguageSourcePortStatus SourcePortStatus => LanguageSourcePortStatus.NotDeclared;
    IDisposable Register(MonacoLanguageService service);
}

/// <summary>Explicit status. InProcessUpstream means a native-language library integration, not a vendored source port.</summary>
public enum LanguageSourcePortStatus { NotDeclared, LocalBaseline, PartialPort, CompletePort, InProcessUpstream }

/// <summary>
/// Legacy opt-in external adapter contract for host applications. None of the built-in
/// language packages implement it; native source ports must register providers directly.
/// Retained only to avoid deleting the generic transport API during migration.
/// </summary>
public interface ILspLanguagePackage : ILanguagePackage
{
    LanguageServerProfile Server { get; }
}

public sealed record LanguageServerProfile(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Description)
{
    public string? NpmPackage { get; init; }
    public object? InitializationOptions { get; init; }
    public object? Configuration { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Releases all language registrations (including providers) in reverse order.</summary>
public sealed class LanguageRegistrations : IDisposable
{
    private List<IDisposable>? _items = new();

    public void Add(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (_items is null) throw new ObjectDisposedException(nameof(LanguageRegistrations));
        _items.Add(registration);
    }

    public void Dispose()
    {
        var items = Interlocked.Exchange(ref _items, null);
        if (items is null) return;
        for (var i = items.Count - 1; i >= 0; i--) items[i].Dispose();
    }
}

/// <summary>Installs independent language packages without making the core depend on them.</summary>
public sealed class LanguagePackageCollection : IDisposable
{
    private readonly MonacoLanguageService _service;
    private readonly Dictionary<string, (ILanguagePackage Package, IDisposable Registrations)> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    internal LanguagePackageCollection(MonacoLanguageService service) => _service = service;

    public IReadOnlyList<ILanguagePackage> Installed
    {
        get { lock (_gate) return _installed.Values.Select(entry => entry.Package).ToArray(); }
    }

    public IDisposable Install(ILanguagePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.Id) || package.LanguageIds.Count == 0)
            throw new ArgumentException("A language package must declare its ID and supported languages.", nameof(package));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_installed.ContainsKey(package.Id))
                throw new InvalidOperationException($"Language package '{package.Id}' is already installed.");
            var registrations = package.Register(_service);
            _installed.Add(package.Id, (package, registrations));
            return new DisposableAction(() => Uninstall(package.Id, registrations));
        }
    }

    private void Uninstall(string id, IDisposable registrations)
    {
        lock (_gate)
        {
            if (_installed.TryGetValue(id, out var installed) && ReferenceEquals(installed.Registrations, registrations))
            {
                _installed.Remove(id);
                registrations.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var item in _installed.Values.Reverse()) item.Registrations.Dispose();
            _installed.Clear();
        }
    }
}

/// <summary>Built-in core registration: plain text only, no hard-coded language dependencies.</summary>
public sealed class PlainTextLanguagePackage : ILanguagePackage
{
    public string Id => "plaintext";
    public IReadOnlyList<string> LanguageIds { get; } = new[] { "plaintext" };

    public IDisposable Register(MonacoLanguageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.Register(new LanguageExtensionPoint("plaintext",
            Aliases: new[] { "Plain Text", "text" }, Extensions: new[] { ".txt" }, Mimetypes: new[] { "text/plain" }));
    }
}
