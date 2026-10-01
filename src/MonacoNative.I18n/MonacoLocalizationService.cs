using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MonacoNative.I18n;

/// <summary>
/// Small runtime localization service whose keys intentionally follow Monaco/VS Code
/// command identifiers whenever an equivalent Monaco operation exists.
/// </summary>
public sealed class MonacoLocalizationService
{
    private const string DefaultCulture = "en-US";
    private readonly Assembly _assembly;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _fallback;
    private IReadOnlyDictionary<string, string> _current;
    private string _cultureName;

    public MonacoLocalizationService(string? cultureName = null)
    {
        _assembly = typeof(MonacoLocalizationService).Assembly;
        _fallback = Load(DefaultCulture);
        _cultureName = CanonicalCulture(cultureName ?? CultureInfo.CurrentUICulture.Name);
        _current = Resolve(_cultureName);
    }

    public event EventHandler? CultureChanged;

    public string CultureName => _cultureName;

    public static IReadOnlyList<string> SupportedCultures { get; } = new[] { "en-US", "zh-CN", "ja-JP", "ko-KR", "qps-ploc" };

    public void SetCulture(string? cultureName)
    {
        var canonical = CanonicalCulture(cultureName);
        if (string.Equals(canonical, _cultureName, StringComparison.OrdinalIgnoreCase))
            return;
        _cultureName = canonical;
        _current = Resolve(canonical);
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_current.TryGetValue(key, out var value))
            return value;
        if (_fallback.TryGetValue(key, out value))
            return value;
        return key;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, GetString(key), args);

    private IReadOnlyDictionary<string, string> Resolve(string cultureName)
    {
        if (string.Equals(cultureName, "qps-ploc", StringComparison.OrdinalIgnoreCase))
            return _fallback.ToDictionary(pair => pair.Key, pair => PseudoLocalizer.Transform(pair.Value), StringComparer.Ordinal);

        var exact = LoadOrNull(cultureName);
        if (exact is not null)
            return exact;

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            if (!string.IsNullOrWhiteSpace(culture.Parent.Name))
            {
                var parent = LoadOrNull(culture.Parent.Name);
                if (parent is not null)
                    return parent;
            }
        }
        catch (CultureNotFoundException)
        {
            // Unknown culture falls back to Monaco's English source strings.
        }

        return _fallback;
    }

    private IReadOnlyDictionary<string, string>? LoadOrNull(string cultureName)
    {
        var canonical = SupportedCultures.FirstOrDefault(c => string.Equals(c, cultureName, StringComparison.OrdinalIgnoreCase));
        return canonical is null || string.Equals(canonical, "qps-ploc", StringComparison.OrdinalIgnoreCase) ? null : Load(canonical);
    }

    private IReadOnlyDictionary<string, string> Load(string cultureName)
    {
        if (_cache.TryGetValue(cultureName, out var cached))
            return cached;

        var resourceName = $"MonacoNative.I18n.Locales.{cultureName}.json";
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded localization resource '{resourceName}'.");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Localization resource '{resourceName}' is invalid.");
        cached = new Dictionary<string, string>(values, StringComparer.Ordinal);
        _cache[cultureName] = cached;
        return cached;
    }

    private static string CanonicalCulture(string? cultureName)
    {
        var normalized = NormalizeAlias(cultureName);
        var exact = SupportedCultures.FirstOrDefault(c => string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        try
        {
            var culture = CultureInfo.GetCultureInfo(normalized);
            while (!string.IsNullOrWhiteSpace(culture.Parent.Name))
            {
                culture = culture.Parent;
                var parent = NormalizeAlias(culture.Name);
                exact = SupportedCultures.FirstOrDefault(c => string.Equals(c, parent, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                    return exact;
            }
        }
        catch (CultureNotFoundException)
        {
        }
        return DefaultCulture;
    }

    private static string NormalizeAlias(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return DefaultCulture;
        if (cultureName.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-SG", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        if (cultureName.Equals("ja", StringComparison.OrdinalIgnoreCase))
            return "ja-JP";
        if (cultureName.Equals("ko", StringComparison.OrdinalIgnoreCase))
            return "ko-KR";
        if (cultureName.Equals("en", StringComparison.OrdinalIgnoreCase))
            return DefaultCulture;
        return cultureName;
    }
}
