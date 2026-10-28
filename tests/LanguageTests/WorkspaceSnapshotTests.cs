using MonacoNative.Language;
using MonacoNative.Language.CSharp;

internal static class WorkspaceSnapshotTests
{
    public static async Task RunAsync()
    {
        var declarationUri = new Uri("inmemory://workspace/Widget.cs");
        var consumerUri = new Uri("inmemory://workspace/Consumer.cs");
        const string declaration = "public class Widget { public int Count; }";
        const string consumer = "public class Consumer { public Widget Current = new Widget(); }";
        IReadOnlyList<NativeLanguageDocument> documents = new[]
        {
            new NativeLanguageDocument(declarationUri, declaration),
            new NativeLanguageDocument(consumerUri, consumer),
        };
        var callbackThread = -1;
        using var engine = new CSharpNativeLanguageService(new CSharpLanguageOptions
        {
            Documents = new Dictionary<Uri, string>
            {
                [new Uri("inmemory://workspace/Fallback.cs")] = "public class Fallback {}",
            },
            DocumentsProvider = () =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                return documents;
            },
        });
        var model = new FakeModel("csharp", declaration) { Uri = declarationUri };
        var renameAt = new LanguagePosition(1, declaration.IndexOf("Widget", StringComparison.Ordinal) + 2);
        var rename = await engine.ProvideRenameEditsAsync(model, renameAt, "Gadget", default);
        AssertEx.True(rename is not null && rename.Edits.Any(edit => edit.Uri == declarationUri) &&
            rename.Edits.Any(edit => edit.Uri == consumerUri),
            "rename includes references in an unopened memory document supplied by the live workspace");

        documents = new[]
        {
            new NativeLanguageDocument(declarationUri, declaration),
            new NativeLanguageDocument(consumerUri, "public class Consumer {}"),
        };
        rename = await engine.ProvideRenameEditsAsync(model, renameAt, "Gadget", default);
        AssertEx.True(rename is not null && rename.Edits.All(edit => edit.Uri == declarationUri),
            "rename uses a peer tab's latest unsaved text instead of an earlier compiler snapshot");

        var consumerModel = new FakeModel("csharp", consumer) { Uri = consumerUri };
        documents = new[]
        {
            new NativeLanguageDocument(declarationUri, declaration),
            new NativeLanguageDocument(consumerUri, consumer),
        };
        var requestingThread = Environment.CurrentManagedThreadId;
        var pending = engine.ProvideDiagnosticsAsync(consumerModel, default);
        AssertEx.Equal(requestingThread, callbackThread, "workspace callback runs before compiler worker dispatch");
        var initialDiagnostics = await pending ?? throw new InvalidOperationException("C# diagnostics unavailable.");
        AssertEx.True(!initialDiagnostics.Any(diagnostic => diagnostic.Code == "CS0246"),
            "peer declaration resolves while its memory document is open");

        documents = new[] { new NativeLanguageDocument(consumerUri, consumer) };
        var diagnostics = await engine.ProvideDiagnosticsAsync(consumerModel, default);
        AssertEx.True(diagnostics?.Any(diagnostic => diagnostic.Code == "CS0246") == true,
            "closing the peer document removes its declaration from the managed workspace");

        var fallback = new FakeModel("csharp", "public class Probe : Fallback {}")
        { Uri = new Uri("inmemory://workspace/Probe.cs") };
        AssertEx.True((await engine.ProvideDiagnosticsAsync(fallback, default))?.Any(diagnostic => diagnostic.Code == "CS0246") == true,
            "DocumentsProvider explicitly overrides fixed Documents");
        Console.WriteLine("PASS live C# workspace: memory rename, changed/closed peers, snapshot capture and precedence");
    }
}
