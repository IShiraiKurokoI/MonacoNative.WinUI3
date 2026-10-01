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
        var tokens = new List<ulong>();
        try
        {
            foreach (var binding in command.Keybindings)
            {
                var when = CombineWhen(command.When, binding.When);
                tokens.Add(_keybindings.Register(command.Id, binding.Chord, when, binding.Weight));
            }
            _bindingTokens[command.Id] = tokens;
        }
        catch
        {
            foreach (var token in tokens)
                _keybindings.Unregister(token);
            _commands.Remove(command.Id);
            throw;
        }
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


    public string GetLabel(EditorCommandDefinition command) => _localization.GetString(command.LabelKey);
    public string GetCategory(EditorCommandDefinition command) => _localization.GetString(command.CategoryKey);

    public void Dispose()
    {
        foreach (var tokens in _bindingTokens.Values)
            foreach (var token in tokens)
                _keybindings.Unregister(token);
        _bindingTokens.Clear();
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
