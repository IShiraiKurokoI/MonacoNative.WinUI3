using System.Text.Json;

namespace MonacoNative.Language.Css;

public sealed record CssPropertyData(string Name, string? Description = null, IReadOnlyList<string>? Values = null);
/// <summary>Property-specific completion data; supports the properties section of VS Code CSS custom data.</summary>
public sealed class CssCustomData
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CssPropertyData> _properties = new(StringComparer.OrdinalIgnoreCase);
    public CssCustomData(bool includeDefaults = true)
    {
        if (!includeDefaults) return;
        foreach (var name in "align-items align-content align-self animation animation-name animation-duration background background-color background-image background-size border border-color border-radius border-width border-style bottom box-shadow box-sizing color content cursor display fill flex flex-basis flex-direction flex-grow flex-shrink flex-wrap float font font-family font-size font-style font-weight gap grid grid-template-columns grid-template-rows height justify-content left letter-spacing line-height list-style margin margin-top margin-right margin-bottom margin-left max-height max-width min-height min-width object-fit opacity order outline overflow overflow-x overflow-y padding padding-top padding-right padding-bottom padding-left position right stroke text-align text-decoration text-overflow top transform transition user-select vertical-align visibility white-space width z-index".Split(' ')) _properties[name] = new(name);
        Set("display", "none block inline inline-block flex inline-flex grid inline-grid contents flow-root table list-item");
        Set("position", "static relative absolute fixed sticky"); Set("flex-direction", "row row-reverse column column-reverse");
        Set("flex-wrap", "nowrap wrap wrap-reverse"); Set("justify-content", "normal start end center flex-start flex-end space-between space-around space-evenly stretch");
        Set("align-items", "normal stretch start end center flex-start flex-end baseline"); Set("font-weight", "normal bold bolder lighter 100 200 300 400 500 600 700 800 900");
        Set("font-style", "normal italic oblique"); Set("box-sizing", "content-box border-box"); Set("visibility", "visible hidden collapse");
        Set("text-align", "start end left right center justify"); Set("white-space", "normal nowrap pre pre-wrap pre-line break-spaces");
        Set("overflow", "visible hidden clip scroll auto"); Set("object-fit", "fill contain cover none scale-down");
        Set("border-style", "none hidden dotted dashed solid double groove ridge inset outset");
        Set("cursor", "auto default none pointer text move not-allowed wait grab grabbing crosshair");
        foreach (var name in new[] { "color", "background-color", "border-color", "fill", "stroke" }) Set(name, "transparent currentColor black white red green blue");
    }
    private void Set(string name, string values) => _properties[name] = new(name, Values: values.Split(' '));
    public IReadOnlyList<CssPropertyData> Properties { get { lock (_gate) return _properties.Values.ToArray(); } }
    public CssPropertyData? Property(string name) { lock (_gate) return _properties.GetValueOrDefault(name); }
    public void Register(string customDataJson)
    {
        using var parsed = JsonDocument.Parse(customDataJson);
        if (!parsed.RootElement.TryGetProperty("properties", out var properties)) return;
        var entries = properties.EnumerateArray().Select(p => new CssPropertyData(p.GetProperty("name").GetString() ?? throw new ArgumentException("Property name missing."), Description(p),
            p.TryGetProperty("values", out var values) ? values.EnumerateArray().Select(v => v.GetProperty("name").GetString()!).ToArray() : Array.Empty<string>())).ToArray();
        lock (_gate) foreach (var entry in entries) _properties[entry.Name] = entry;
    }
    private static string? Description(JsonElement value)
    {
        if (!value.TryGetProperty("description", out var description)) return null;
        return description.ValueKind == JsonValueKind.String ? description.GetString() : description.TryGetProperty("value", out var markdown) ? markdown.GetString() : null;
    }
}
