using MonacoNative.Core;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

/// <summary>Document measurements captured while the editor is still running.</summary>
public readonly record struct EditorDocumentStatus(
    ulong VersionId, nuint Length, nuint LineCount, long CharacterCount);

/// <summary>Basic language capabilities for the editor's active document.</summary>
public readonly record struct EditorLanguageStatus(
    string LanguageId, bool HasTokenizer, LanguageSourcePortStatus SourcePortStatus);

public sealed partial class MonacoEditor
{
    private ulong _statusCharacterVersion = ulong.MaxValue;
    private long _statusCharacterCount;

    /// <summary>
    /// Read a consistent document status without exposing the native model to host UI.
    /// Returns false once shutdown starts, including when the model has been disposed.
    /// </summary>
    public bool TryGetDocumentStatus(out EditorDocumentStatus status)
    {
        status = default;
        if (IsDisposed) return false;
        try
        {
            var version = _model.VersionId;
            var length = _model.Length;
            var lineCount = _model.LineCount;
            if (_statusCharacterVersion != version)
            {
                var characters = MonacoTextStatistics.CountCharacters(_model.Text);
                if (IsDisposed || _model.VersionId != version)
                    return false;
                _statusCharacterCount = characters;
                _statusCharacterVersion = version;
            }
            if (IsDisposed) return false;
            status = new EditorDocumentStatus(version, length, lineCount, _statusCharacterCount);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Read active-language capabilities without requiring the host to inspect packages.</summary>
    public bool TryGetLanguageStatus(out EditorLanguageStatus status)
    {
        status = default;
        if (IsDisposed) return false;
        try
        {
            var languageId = _model.LanguageId;
            var hasTokenizer = _languages.Languages.TryGetMonarchTokenizer(languageId, out _, out _);
            var port = _languages.Packages.Installed.FirstOrDefault(package =>
                package.LanguageIds.Contains(languageId, StringComparer.OrdinalIgnoreCase))
                ?.SourcePortStatus ?? LanguageSourcePortStatus.NotDeclared;
            if (IsDisposed) return false;
            status = new EditorLanguageStatus(languageId, hasTokenizer, port);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
