using MonacoNative.Core;
using MonacoNative.I18n;

namespace MonacoNative.Renderer.WinUI3;

internal readonly record struct EditorKeybinding(string Chord, int Weight = 0, string? When = null);

internal sealed record EditorCommandDefinition(
    string Id,
    string LabelKey,
    string CategoryKey,
    string When,
    Func<ValueTask> Execute,
    bool ShowInContextMenu,
    int MenuOrder,
    EditorKeybinding[] Keybindings);

internal sealed class EditorCommandService : IDisposable
{
    private readonly MonacoKeybindingService _keybindings = new();
    private readonly MonacoLocalizationService _localization;
    private readonly Dictionary<string, EditorCommandDefinition> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ulong>> _bindingTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<EditorShortcutBinding>> _overrides = new(StringComparer.Ordinal);
    public EditorShortcutScheme ShortcutScheme { get; private set; } = EditorShortcutScheme.VSCode;

    public void SetShortcutScheme(EditorShortcutScheme scheme)
    {
        if (!Enum.IsDefined(scheme)) throw new ArgumentOutOfRangeException(nameof(scheme));
        if (ShortcutScheme == scheme) return;
        var previous = ShortcutScheme;
        ShortcutScheme = scheme;
        try { RebuildBindings(); }
        catch
        {
            ShortcutScheme = previous;
            RebuildBindings();
            throw;
        }
    }

    // An empty override removes all shortcuts for this command without disabling its menu action.
    // Revert a rejected chord atomically; a host typo must not unbind the editor.
    public void SetShortcutOverride(string id, IReadOnlyList<EditorShortcutBinding>? shortcuts)
    {
        if (!_commands.ContainsKey(id)) throw new ArgumentException($"Unknown editor command: {id}", nameof(id));
        EditorShortcutBinding[]? replacement = null;
        if (shortcuts is not null)
        {
            replacement = shortcuts.ToArray();
            foreach (var shortcut in replacement)
                if (shortcut is null || string.IsNullOrWhiteSpace(shortcut.Chord))
                    throw new ArgumentException("A shortcut chord must not be empty.", nameof(shortcuts));
        }

        var hadPrevious = _overrides.TryGetValue(id, out var previous);
        if (replacement is null) _overrides.Remove(id);
        else _overrides[id] = replacement;
        try { RebuildBindings(); }
        catch
        {
            if (hadPrevious) _overrides[id] = previous!;
            else _overrides.Remove(id);
            RebuildBindings();
            throw;
        }
    }

    public IReadOnlyList<EditorKeybinding> EffectiveBindings(string id)
    {
        if (!_commands.TryGetValue(id, out var command)) return Array.Empty<EditorKeybinding>();
        if (_overrides.TryGetValue(id, out var bindings))
            return bindings.Select(b => new EditorKeybinding(b.Chord, b.Weight, b.When)).ToArray();
        var mapped = EditorShortcutProfiles.Resolve(ShortcutScheme, id);
        return mapped is null ? command.Keybindings : mapped.Select(chord => new EditorKeybinding(chord, 400)).ToArray();
    }

    private void RebuildBindings()
    {
        ResetChord();
        foreach (var tokens in _bindingTokens.Values)
            foreach (var token in tokens) _keybindings.Unregister(token);
        _bindingTokens.Clear();
        foreach (var command in _commands.Values)
            BindCommand(command);
    }

    private void BindCommand(EditorCommandDefinition command)
    {
        var tokens = new List<ulong>();
        try
        {
            foreach (var binding in EffectiveBindings(command.Id))
                tokens.Add(_keybindings.Register(command.Id, binding.Chord,
                    CombineWhen(command.When, binding.When), binding.Weight));
            _bindingTokens[command.Id] = tokens;
        }
        catch
        {
            foreach (var token in tokens) _keybindings.Unregister(token);
            throw;
        }
    }
    private readonly Dictionary<string, object> _contextCache = new(StringComparer.Ordinal);

    // Evaluated only on an actual command dispatch, never on the ordinary printable-key fast path.
    public Func<string, bool>? IsCommandAllowed { get; set; }
    public IReadOnlyList<EditorCommandDefinition> Commands => _commands.Values.ToArray();
    public bool TryGetCommand(string id, out EditorCommandDefinition? command) => _commands.TryGetValue(id, out command);

    public EditorCommandService(MonacoLocalizationService localization) => _localization = localization;

    // PendingChord is updated from the same resolve snapshot that changes native chord state,
    // so the WinUI text-input fast path can test chord mode without a P/Invoke per keydown.
    public bool InChordMode => !string.IsNullOrEmpty(PendingChord);
    public string PendingChord { get; private set; } = string.Empty;

    public void SetContext(string key, bool value)
    {
        if (_contextCache.TryGetValue(key, out var current) && current is bool cached && cached == value) return;
        _keybindings.SetContext(key, value);
        _contextCache[key] = value;
    }

    public void SetContext(string key, double value)
    {
        if (_contextCache.TryGetValue(key, out var current) && current is double cached && cached.Equals(value)) return;
        _keybindings.SetContext(key, value);
        _contextCache[key] = value;
    }

    public void SetContext(string key, string value)
    {
        value ??= string.Empty;
        if (_contextCache.TryGetValue(key, out var current) && current is string cached && string.Equals(cached, value, StringComparison.Ordinal)) return;
        _keybindings.SetContext(key, value);
        _contextCache[key] = value;
    }
    public bool ContextMatches(string? expression) => _keybindings.ContextMatches(expression);

    public void Register(EditorCommandDefinition command)
    {
        if (!_commands.TryAdd(command.Id, command))
            throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
        try { BindCommand(command); }
        catch { _commands.Remove(command.Id); throw; }
    }

    public async ValueTask<bool> TryExecuteAsync(string commandId)
    {
        if (!_commands.TryGetValue(commandId, out var command) ||
            IsCommandAllowed?.Invoke(commandId) == false || !ContextMatches(command.When))
            return false;
        await command.Execute();
        return true;
    }

    public async ValueTask<MonacoKeybindingResult> ResolveAndExecuteAsync(string key, MonacoKeyModifiers modifiers)
    {
        var result = _keybindings.Resolve(key, modifiers);
        PendingChord = result.PendingChord;
        if (result.Kind == MonacoKeybindingResultKind.Command && !string.IsNullOrEmpty(result.CommandId))
            await ExecuteResolvedAsync(result.CommandId);
        if (result.Kind is MonacoKeybindingResultKind.Command or MonacoKeybindingResultKind.ChordCancelled or MonacoKeybindingResultKind.NoMatch)
            PendingChord = string.Empty;
        return result;
    }

    private async ValueTask ExecuteResolvedAsync(string commandId)
    {
        // The native keybinding resolver already evaluated CombineWhen(command.When,
        // binding.When) for the winning rule. Re-checking command.When here would add another
        // managed/native crossing and could observe a different context snapshot mid-dispatch.
        if (IsCommandAllowed?.Invoke(commandId) != false && _commands.TryGetValue(commandId, out var command))
            await command.Execute();
    }

    public void ResetChord()
    {
        _keybindings.ResetChord();
        PendingChord = string.Empty;
    }

    public IReadOnlyList<EditorCommandDefinition> ContextMenuCommands() =>
        _commands.Values
            .Where(command => command.ShowInContextMenu)
            .OrderBy(command => command.CategoryKey, StringComparer.Ordinal)
            .ThenBy(command => command.MenuOrder)
            .ThenBy(command => command.Id, StringComparer.Ordinal)
            .ToArray();


    // The menu always displays the active profile or host override, not the
    // command's original default chord. Formatting stays with command bindings.
    public string GetShortcutLabel(EditorCommandDefinition command)
    {
        var preferred = EffectiveBindings(command.Id)
            .OrderByDescending(binding => binding.Weight)
            .FirstOrDefault();
        return EditorShortcutLabelFormatter.Format(preferred.Chord);
    }

    public string GetLabel(EditorCommandDefinition command) => _localization.GetString(command.LabelKey);
    public string GetCategory(EditorCommandDefinition command) => _localization.GetString(command.CategoryKey);

    public void Dispose()
    {
        foreach (var tokens in _bindingTokens.Values)
            foreach (var token in tokens)
                _keybindings.Unregister(token);
        _bindingTokens.Clear();
        _overrides.Clear();
        _commands.Clear();
        _contextCache.Clear();
        _keybindings.Dispose();
    }

    private static string CombineWhen(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second ?? string.Empty;
        if (string.IsNullOrWhiteSpace(second)) return first;
        return $"({first}) && ({second})";
    }
}
