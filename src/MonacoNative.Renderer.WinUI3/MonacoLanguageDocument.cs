using MonacoNative.Core;
using MonacoNative.Language;

namespace MonacoNative.Renderer.WinUI3;

internal sealed class MonacoLanguageDocument : ILanguageTextModel
{
    private readonly MonacoTextModel _model;
    public MonacoLanguageDocument(MonacoTextModel model, Uri? uri = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Uri = uri ?? new Uri("inmemory://model/1");
    }

    public Uri Uri { get; set; }
    public string LanguageId => _model.LanguageId;
    public ulong VersionId => _model.VersionId;
    public int LineCount => checked((int)_model.LineCount);
    public string GetValue() => _model.Text;
    public string GetLineContent(int lineNumber) => _model.GetLineContent(lineNumber);
    public int GetLineMaxColumn(int lineNumber) => _model.GetLineMaxColumn(lineNumber);
}
