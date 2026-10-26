using System.Text.Json;

namespace MonacoNative.Language.Html;

public sealed record HtmlAttributeData(string Name, string? Description = null, bool Boolean = false, IReadOnlyList<string>? Values = null);
public sealed record HtmlTagData(string Name, string? Description = null, IReadOnlyList<HtmlAttributeData>? Attributes = null);

/// <summary>HTML metadata, including the tags/globalAttributes/valueSets sections of VS Code custom data.</summary>
public sealed class HtmlCustomData
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HtmlTagData> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HtmlAttributeData> _global = new(StringComparer.OrdinalIgnoreCase);
    public HtmlCustomData(bool includeDefaults = true)
    {
        if (!includeDefaults) return;
        foreach (var name in ("a abbr address area article aside audio b base bdi bdo blockquote body br button canvas caption cite code col colgroup data datalist dd del details dfn dialog div dl dt em embed fieldset figcaption figure footer form h1 h2 h3 h4 h5 h6 head header hgroup hr html i iframe img input ins kbd label legend li link main map mark menu meta meter nav noscript object ol optgroup option output p picture pre progress q rp rt ruby s samp script search section select slot small source span strong style sub summary sup table tbody td template textarea tfoot th thead time title tr track u ul var video wbr").Split(' '))
            _tags[name] = new(name);
        foreach (var name in "id class title style lang dir hidden tabindex role slot accesskey contenteditable draggable spellcheck translate aria-label aria-labelledby aria-describedby aria-hidden aria-live data-".Split(' ')) _global[name] = new(name);
        _global["dir"] = new("dir", Values: new[] { "ltr", "rtl", "auto" });
        _global["hidden"] = new("hidden", Boolean: true);
        foreach (var name in new[] { "contenteditable", "draggable", "spellcheck", "aria-hidden" }) _global[name] = new(name, Values: new[] { "true", "false" });
        _global["aria-live"] = new("aria-live", Values: new[] { "off", "polite", "assertive" });
        Add("input", "type name value placeholder autocomplete min max step pattern accept width height", "disabled checked required readonly multiple autofocus");
        Add("button", "type name value form", "disabled autofocus"); Add("a", "href target rel download hreflang type", "");
        Add("img", "src srcset alt width height loading decoding", "ismap"); Add("form", "action method enctype target autocomplete", "novalidate");
        Add("label", "for", ""); Add("select", "name size form", "disabled required multiple autofocus"); Add("option", "value label", "disabled selected");
        Add("textarea", "name rows cols placeholder maxlength wrap", "disabled readonly required autofocus");
        Add("script", "src type crossorigin integrity referrerpolicy", "async defer nomodule"); Add("link", "href rel type media crossorigin integrity", "disabled");
        Add("meta", "charset name content http-equiv", ""); Add("html", "xmlns", ""); Add("td", "colspan rowspan headers", ""); Add("th", "colspan rowspan headers scope", "");
        Add("audio", "src preload crossorigin", "autoplay controls loop muted"); Add("video", "src poster width height preload crossorigin", "autoplay controls loop muted playsinline");
        Add("iframe", "src srcdoc width height loading sandbox allow", "allowfullscreen"); Add("source", "src srcset type media sizes", "");
        SetValues("input", "type", "button checkbox color date datetime-local email file hidden image month number password radio range reset search submit tel text time url week");
        SetValues("button", "type", "button reset submit"); SetValues("form", "method", "get post dialog");
        SetValues("a", "target", "_self _blank _parent _top"); SetValues("img", "loading", "eager lazy");
        SetValues("textarea", "wrap", "hard soft off"); SetValues("script", "type", "module text/javascript application/ld+json");
    }
    private void Add(string tag, string attributes, string boolean)
    {
        var values = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(name => new HtmlAttributeData(name))
            .Concat(boolean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(name => new HtmlAttributeData(name, Boolean: true))).ToArray();
        _tags[tag] = new(tag, Attributes: values);
    }
    private void SetValues(string tag, string name, string values)
    {
        var entry = _tags[tag];
        _tags[tag] = entry with { Attributes = (entry.Attributes ?? Array.Empty<HtmlAttributeData>()).Select(a => a.Name == name ? a with { Values = values.Split(' ') } : a).ToArray() };
    }
    public IReadOnlyList<HtmlTagData> Tags { get { lock (_gate) return _tags.Values.ToArray(); } }
    public IReadOnlyList<HtmlAttributeData> AttributesFor(string tag)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, HtmlAttributeData>(_global, StringComparer.OrdinalIgnoreCase);
            if (_tags.TryGetValue(tag, out var data)) foreach (var attribute in data.Attributes ?? Array.Empty<HtmlAttributeData>()) result[attribute.Name] = attribute;
            return result.Values.ToArray();
        }
    }
    public void Register(string customDataJson)
    {
        using var document = JsonDocument.Parse(customDataJson);
        var root = document.RootElement;
        var sets = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("valueSets", out var valueSets))
            foreach (var set in valueSets.EnumerateArray()) sets[set.GetProperty("name").GetString()!] = Values(set);
        HtmlAttributeData Attribute(JsonElement value)
        {
            var name = value.GetProperty("name").GetString() ?? throw new ArgumentException("HTML attribute name is missing.");
            var setName = value.TryGetProperty("valueSet", out var set) ? set.GetString() : null;
            return new(name, Description(value), setName == "v", Values(value).Concat(setName is not null && sets.TryGetValue(setName, out var named) ? named : Array.Empty<string>()).Distinct().ToArray());
        }
        // Validate the whole input before committing any part of it.
        var globals = root.TryGetProperty("globalAttributes", out var global) ? global.EnumerateArray().Select(Attribute).ToArray() : Array.Empty<HtmlAttributeData>();
        var tags = root.TryGetProperty("tags", out var list) ? list.EnumerateArray().Select(tag => new HtmlTagData(
            tag.GetProperty("name").GetString() ?? throw new ArgumentException("HTML tag name is missing."), Description(tag),
            tag.TryGetProperty("attributes", out var attrs) ? attrs.EnumerateArray().Select(Attribute).ToArray() : Array.Empty<HtmlAttributeData>())).ToArray() : Array.Empty<HtmlTagData>();
        lock (_gate) { foreach (var attribute in globals) _global[attribute.Name] = attribute; foreach (var tag in tags) _tags[tag.Name] = tag; }
    }
    private static string[] Values(JsonElement element) => element.TryGetProperty("values", out var values)
        ? values.EnumerateArray().Select(value => value.GetProperty("name").GetString()).Where(value => value is not null).Cast<string>().ToArray() : Array.Empty<string>();
    private static string? Description(JsonElement element)
    {
        if (!element.TryGetProperty("description", out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.TryGetProperty("value", out var text) ? text.GetString() : null;
    }
}
