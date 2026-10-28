using System.Runtime.InteropServices;
using MonacoNative.Core;
using MonacoNative.Documents;
using MonacoNative.Language;
using MonacoNative.Renderer.WinUI3;

namespace MonacoNative.DocumentModels.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); ++_passed; Console.WriteLine($"[PASS] {name}"); }
        catch (Exception error) { ++_failed; Console.Error.WriteLine($"[FAIL] {name}: {error}"); }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void True(bool actual, string message)
    {
        if (!actual) throw new InvalidOperationException(message);
    }

    private static async Task Throws<T>(Func<Task> test) where T : Exception
    {
        try { await test(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class OpenDocument : IDisposable
    {
        public MonacoEditorDocument Document { get; }

        private OpenDocument(IEditorDocumentProvider provider, EditorDocumentContent content)
        {
            var model = new MonacoTextModel(content.Text, content.LanguageId ?? provider.LanguageId ?? "plaintext");
            Document = new MonacoEditorDocument(provider, model, new MonacoViewModel(model),
                new MonacoLanguageDocument(model, provider.Uri), content.Text);
        }

        public static async Task<OpenDocument> CreateAsync(IEditorDocumentProvider provider) =>
            new(provider, await provider.ReadAsync());

        public static Task<OpenDocument> MemoryAsync(string text, string name) =>
            CreateAsync(new MemoryDocumentProvider(text, name));

        public void Dispose() => Document.Close();
    }

    private sealed class DelayedProvider : IEditorDocumentProvider
    {
        private readonly MemoryDocumentProvider _inner = new("start", "slow.txt");
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Uri => _inner.Uri;
        public string DisplayName => _inner.DisplayName;
        public string? LanguageId => _inner.LanguageId;
        public bool IsReadOnly => false;
        public string Text => _inner.Text;
        public void Release() => _release.TrySetResult();
        public Task<EditorDocumentContent> ReadAsync(CancellationToken token = default) => _inner.ReadAsync(token);
        public async Task WriteAsync(string text, CancellationToken token = default)
        {
            await _release.Task.WaitAsync(token);
            await _inner.WriteAsync(text, token);
        }
    }

    private static EditorRequestIdentity RequestIdentity(MonacoEditorDocument document, long activation,
        MonacoPosition? anchor = null, MonacoPosition? caret = null) =>
        new(document.Model, activation, document.Uri, document.LanguageId, document.Model.VersionId,
            anchor ?? new MonacoPosition(1, 1), caret ?? new MonacoPosition(1, 1));

    public static async Task<int> Main()
    {
        var nativePath = Environment.GetEnvironmentVariable("MONACONATIVE_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(nativePath))
        {
            var fullPath = Path.GetFullPath(nativePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Native core library not found.", fullPath);
            NativeLibrary.SetDllImportResolver(typeof(MonacoTextModel).Assembly,
                (name, assembly, searchPath) => name == "MonacoNative.Core.Native.dll"
                    ? NativeLibrary.Load(fullPath) : IntPtr.Zero);
        }

        await Run("independent document models preserve interleaved undo and redo histories", async () =>
        {
            using var first = await OpenDocument.MemoryAsync("alpha", "first.txt");
            using var second = await OpenDocument.MemoryAsync("beta", "second.txt");
            first.Document.Model.Replace(5, 5, "1");
            second.Document.Model.Replace(4, 4, "2");
            first.Document.Model.Replace(6, 6, "3");
            True(first.Document.Model.Undo(), "First document undo failed.");
            Equal("alpha1", first.Document.Model.Text);
            Equal("beta2", second.Document.Model.Text);
            True(second.Document.Model.Undo(), "Second document undo failed.");
            Equal("beta", second.Document.Model.Text);
            Equal(false, second.Document.IsDirty);
            Equal(true, first.Document.IsDirty);
            True(first.Document.Model.Redo(), "First document redo failed.");
            Equal("alpha13", first.Document.Model.Text);
            Equal("beta", second.Document.Model.Text);
        });

        await Run("same-length changes mark dirty and undo restores the saved baseline", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("abcdef", "same-length.txt");
            var doc = opened.Document;
            Equal(false, doc.IsDirty);
            doc.Model.Replace(0, 3, "XYZ");
            Equal((nuint)6, doc.Model.Length);
            Equal(true, doc.IsDirty);
            True(doc.Model.Undo(), "Undo failed.");
            Equal("abcdef", doc.Model.Text);
            Equal(false, doc.IsDirty);
            True(doc.Model.Redo(), "Redo failed.");
            Equal(true, doc.IsDirty);
        });

        await Run("dirty notifications change only when save-baseline equality changes", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("a", "notify.txt");
            var doc = opened.Document;
            var transitions = new List<bool>();
            doc.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MonacoEditorDocument.IsDirty)) transitions.Add(doc.IsDirty);
            };
            doc.Model.Replace(1, 1, "b");
            doc.Model.Replace(2, 2, "c");
            doc.MarkSaved(doc.Model.Text);
            doc.Model.Replace(3, 3, "d");
            True(doc.Model.Undo(), "Undo-to-save failed.");
            Equal("True,False,True,False", string.Join(',', transitions));
        });

        await Run("saving does not clear native undo history", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("one", "saved.txt");
            var doc = opened.Document;
            doc.Model.Replace(3, 3, " two");
            var snapshot = doc.Model.Text;
            await doc.Provider.WriteAsync(snapshot);
            doc.MarkSaved(snapshot);
            Equal(false, doc.IsDirty);
            True(doc.Model.CanUndo, "Save erased the document's undo history.");
            True(doc.Model.Undo(), "Undo after save failed.");
            Equal("one", doc.Model.Text);
            Equal(true, doc.IsDirty);
            True(doc.Model.Redo(), "Redo-to-save failed.");
            Equal(false, doc.IsDirty);
        });

        await Run("save completion leaves edits made during provider I/O dirty", async () =>
        {
            var provider = new DelayedProvider();
            using var opened = await OpenDocument.CreateAsync(provider);
            var doc = opened.Document;
            doc.Model.Replace(5, 5, " saved");
            var snapshot = doc.Model.Text;
            var pending = provider.WriteAsync(snapshot);
            True(!pending.IsCompleted, "Provider did not delay its write.");
            doc.Model.Replace(doc.Model.Length, doc.Model.Length, " later");
            provider.Release();
            await pending;
            doc.MarkSaved(snapshot);
            Equal("start saved", provider.Text);
            Equal("start saved later", doc.Model.Text);
            Equal(true, doc.IsDirty);
            True(doc.Model.Undo(), "Undo of later edit failed.");
            Equal(false, doc.IsDirty);
        });

        await Run("rejected provider save retains working copy and dirty state", async () =>
        {
            var provider = new MemoryDocumentProvider("original", "readonly.txt", isReadOnly: true);
            using var opened = await OpenDocument.CreateAsync(provider);
            var doc = opened.Document;
            // Host-side model writes may bypass UI read-only policy; they must still be safe to save.
            doc.Model.Replace(0, doc.Model.Length, "unsaved");
            await Throws<UnauthorizedAccessException>(() => provider.WriteAsync(doc.Model.Text));
            Equal("original", provider.Text);
            Equal("unsaved", doc.Model.Text);
            Equal(true, doc.IsDirty);
            Equal(true, doc.IsReadOnly);
        });

        await Run("changing provider after Save As preserves model, text and undo history", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "old.txt");
            var doc = opened.Document;
            var model = doc.Model;
            doc.Model.Replace(3, 3, " changed");
            var destination = new MemoryDocumentProvider("", "renamed.txt");
            var snapshot = doc.Model.Text;
            await destination.WriteAsync(snapshot);
            doc.SetProvider(destination);
            doc.MarkSaved(snapshot);
            Equal(destination.Uri, doc.Uri);
            Equal("renamed.txt", doc.DisplayName);
            Equal(destination.Uri, doc.LanguageDocument.Uri);
            True(ReferenceEquals(model, doc.Model), "Save As replaced the native model.");
            Equal(false, doc.IsDirty);
            True(doc.Model.Undo(), "Undo after Save As failed.");
            Equal("old", doc.Model.Text);
            Equal(true, doc.IsDirty);
        });

        await Run("cross-document grouped edits dirty each document with independent undo", async () =>
        {
            using var definition = await OpenDocument.MemoryAsync("let old = old;", "definition.ts");
            using var reference = await OpenDocument.MemoryAsync("old();", "reference.ts");
            using (var edit = definition.Document.Model.BeginUndoTransaction())
            {
                definition.Document.Model.Replace(10, 13, "renamed");
                definition.Document.Model.Replace(4, 7, "renamed");
                edit.Complete();
            }
            using (var edit = reference.Document.Model.BeginUndoTransaction())
            {
                reference.Document.Model.Replace(0, 3, "renamed");
                edit.Complete();
            }
            Equal("let renamed = renamed;", definition.Document.Model.Text);
            Equal("renamed();", reference.Document.Model.Text);
            Equal(true, definition.Document.IsDirty);
            Equal(true, reference.Document.IsDirty);
            True(definition.Document.Model.Undo(), "Definition undo failed.");
            Equal("let old = old;", definition.Document.Model.Text);
            Equal(false, definition.Document.IsDirty);
            Equal("renamed();", reference.Document.Model.Text);
            True(reference.Document.Model.Undo(), "Reference undo failed.");
            Equal(false, reference.Document.IsDirty);
        });

        await Run("language snapshots continue to reference their own native model", async () =>
        {
            using var first = await OpenDocument.MemoryAsync("alpha\r\nsecond", "first.cs");
            using var second = await OpenDocument.MemoryAsync("beta", "second.json");
            Equal(first.Document.Uri, first.Document.LanguageDocument.Uri);
            Equal("csharp", first.Document.LanguageDocument.LanguageId);
            Equal("json", second.Document.LanguageDocument.LanguageId);
            second.Document.Model.Replace(0, 4, "changed");
            Equal("alpha\r\nsecond", first.Document.LanguageDocument.GetValue());
            Equal("changed", second.Document.LanguageDocument.GetValue());
            Equal(2, first.Document.LanguageDocument.LineCount);
        });

        await Run("workspace plans apply different-length replacements against original offsets", async () =>
        {
            using var first = await OpenDocument.MemoryAsync("old + old", "plan-first.ts");
            using var second = await OpenDocument.MemoryAsync("use(old)", "plan-second.ts");
            var firstPlan = new EditorWorkspaceEditPlan(first.Document,
            [
                new(first.Document.Uri, new(1, 1, 1, 4), "longerName"),
                new(first.Document.Uri, new(1, 7, 1, 10), "longerName"),
            ]);
            var secondPlan = new EditorWorkspaceEditPlan(second.Document,
                [new(second.Document.Uri, new(1, 5, 1, 8), "longerName")]);
            firstPlan.ValidateCurrent();
            secondPlan.ValidateCurrent();
            using (var edit = first.Document.Model.BeginUndoTransaction())
            { firstPlan.Apply(); edit.Complete(); }
            using (var edit = second.Document.Model.BeginUndoTransaction())
            { secondPlan.Apply(); edit.Complete(); }
            Equal("longerName + longerName", first.Document.Model.Text);
            Equal("use(longerName)", second.Document.Model.Text);
            Equal(true, first.Document.IsDirty);
            Equal(true, second.Document.IsDirty);
            True(first.Document.Model.Undo(), "Grouped workspace undo failed.");
            Equal("old + old", first.Document.Model.Text);
            Equal(false, first.Document.IsDirty);
            Equal("use(longerName)", second.Document.Model.Text);
        });

        await Run("workspace plan rejects overlapping edits before mutating a document", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("abcdef", "overlap.ts");
            var doc = opened.Document;
            await Throws<ArgumentException>(() =>
            {
                _ = new EditorWorkspaceEditPlan(doc,
                    [new(doc.Uri, new(1, 2, 1, 5), "one"), new(doc.Uri, new(1, 4, 1, 6), "two")]);
                return Task.CompletedTask;
            });
            Equal("abcdef", doc.Model.Text);
            Equal(false, doc.IsDirty);
            Equal(false, doc.Model.CanUndo);
        });

        await Run("workspace plan rejects out-of-range and reversed ranges without clamping", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("abc\ndef", "ranges.ts");
            var doc = opened.Document;
            LanguageRange[] invalid =
            [
                new(0, 1, 1, 2), new(1, 0, 1, 2), new(1, 1, 3, 1),
                new(2, 1, 1, 2), new(1, 4, 1, 2), new(1, 1, 1, 8),
            ];
            foreach (var range in invalid)
            {
                await Throws<ArgumentException>(() =>
                {
                    _ = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, range, "replacement")]);
                    return Task.CompletedTask;
                });
            }
            Equal("abc\ndef", doc.Model.Text);
            Equal(false, doc.IsDirty);
        });

        await Run("workspace plan rejects a stale model version", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "stale.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
            doc.Model.Replace(0, 3, "hostEdit");
            await Throws<InvalidOperationException>(() => { plan.ValidateCurrent(); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => { plan.Apply(); return Task.CompletedTask; });
            Equal("hostEdit", doc.Model.Text);
        });

        await Run("Save As invalidates a workspace plan prepared for the previous URI", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "identity-before.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
            var version = doc.Model.VersionId;
            doc.SetProvider(new MemoryDocumentProvider("old", "identity-after.ts"));
            Equal(version, doc.Model.VersionId);
            await Throws<InvalidOperationException>(() => { plan.ValidateCurrent(); return Task.CompletedTask; });
            Equal("old", doc.Model.Text);
            Equal(false, doc.IsDirty);
        });

        await Run("changing only the language invalidates an already prepared workspace edit", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "language-before.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
            var version = doc.Model.VersionId;
            doc.Model.LanguageId = "plaintext";
            Equal(version, doc.Model.VersionId);
            await Throws<InvalidOperationException>(() => { plan.Apply(); return Task.CompletedTask; });
            Equal("old", doc.Model.Text);
            Equal(false, doc.IsDirty);
        });

        await Run("workspace plan respects read-only policy both at preparation and commit", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "protected.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
            doc.SetReadOnly(true);
            await Throws<InvalidOperationException>(() => { plan.ValidateCurrent(); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() =>
            {
                _ = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
                return Task.CompletedTask;
            });
            Equal("old", doc.Model.Text);
            Equal(false, doc.IsDirty);
        });

        await Run("closing a document invalidates a prepared workspace edit", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old", "closed.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc, [new(doc.Uri, new(1, 1, 1, 4), "new")]);
            doc.Close();
            await Throws<InvalidOperationException>(() => { plan.ValidateCurrent(); return Task.CompletedTask; });
        });

        await Run("rolling back a grouped workspace edit returns dirty state to saved content", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("old old", "rollback.ts");
            var doc = opened.Document;
            var plan = new EditorWorkspaceEditPlan(doc,
                [new(doc.Uri, new(1, 1, 1, 4), "new"), new(doc.Uri, new(1, 5, 1, 8), "new")]);
            using (var transaction = doc.Model.BeginUndoTransaction())
            {
                plan.ValidateCurrent();
                plan.Apply();
                Equal(true, doc.IsDirty);
                transaction.Rollback();
            }
            Equal("old old", doc.Model.Text);
            Equal(false, doc.IsDirty);
        });

        await Run("workspace apply catches reentrant edits to a later document and permits rollback", async () =>
        {
            using var first = await OpenDocument.MemoryAsync("old", "reentry-first.ts");
            using var second = await OpenDocument.MemoryAsync("old", "reentry-second.ts");
            var firstPlan = new EditorWorkspaceEditPlan(first.Document,
                [new(first.Document.Uri, new(1, 1, 1, 4), "new")]);
            var secondPlan = new EditorWorkspaceEditPlan(second.Document,
                [new(second.Document.Uri, new(1, 1, 1, 4), "new")]);
            var callbackRan = false;
            first.Document.Model.Changed += (_, _) =>
            {
                if (callbackRan) return;
                callbackRan = true;
                second.Document.Model.Replace(0, 3, "host-change");
            };
            firstPlan.ValidateCurrent();
            secondPlan.ValidateCurrent();
            using (var firstTransaction = first.Document.Model.BeginUndoTransaction())
            using (var secondTransaction = second.Document.Model.BeginUndoTransaction())
            {
                firstPlan.Apply();
                Equal("host-change", second.Document.Model.Text);
                await Throws<InvalidOperationException>(() => { secondPlan.Apply(); return Task.CompletedTask; });
                secondTransaction.Rollback();
                firstTransaction.Rollback();
            }
            Equal(true, callbackRan);
            Equal("old", first.Document.Model.Text);
            Equal("old", second.Document.Model.Text);
            Equal(false, first.Document.IsDirty);
            Equal(false, second.Document.IsDirty);
        });

        await Run("clearing a filesystem read-only attribute allows the document to become writable", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"monaconative-readonly-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(path, "text");
                var attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                using var opened = await OpenDocument.CreateAsync(new FileDocumentProvider(path));
                var doc = opened.Document;
                Equal(true, doc.IsReadOnly);
                File.SetAttributes(path, attributes);
                Equal(false, doc.IsReadOnly);
                doc.SetReadOnly(true);
                Equal(true, doc.IsReadOnly);
                doc.SetReadOnly(false);
                Equal(false, doc.IsReadOnly);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
        });

        await Run("closing one document disposes only its native resources and is idempotent", async () =>
        {
            using var first = await OpenDocument.MemoryAsync("first", "first.txt");
            using var second = await OpenDocument.MemoryAsync("second", "second.txt");
            var closedEvents = 0;
            first.Document.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MonacoEditorDocument.IsClosed)) ++closedEvents;
            };
            first.Document.Close();
            first.Document.Close();
            Equal(true, first.Document.IsClosed);
            Equal(1, closedEvents);
            await Throws<ObjectDisposedException>(() => { _ = first.Document.Model.Text; return Task.CompletedTask; });
            await Throws<ObjectDisposedException>(() => { _ = first.Document.ViewModel.ViewLineCount; return Task.CompletedTask; });
            Equal("second", second.Document.Model.Text);
            second.Document.Model.Replace(6, 6, " remains open");
            Equal("second remains open", second.Document.Model.Text);
            Equal(true, second.Document.IsDirty);
        });

        await Run("late request rejects a reopened resource even when URI version and caret are identical", async () =>
        {
            var uri = new Uri("inmemory://request-safety/reopened.txt");
            using var first = await OpenDocument.CreateAsync(new MemoryDocumentProvider("same", "reopened.txt", uri: uri));
            var request = RequestIdentity(first.Document, 1);
            var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = response.Task; // A provider is allowed to finish after cancellation.
            first.Document.Close();
            using var reopened = await OpenDocument.CreateAsync(new MemoryDocumentProvider("same", "reopened.txt", uri: uri));
            var current = RequestIdentity(reopened.Document, 1);
            Equal(request.Uri, current.Uri);
            Equal(request.Version, current.Version);
            response.SetResult("late result");
            Equal("late result", await pending);
            Equal(false, request.Matches(current, checkSelection: true));
            Equal("same", reopened.Document.Model.Text);
        });

        await Run("switching away and back invalidates requests without discarding the retained model", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("unchanged", "activation.txt");
            var request = RequestIdentity(opened.Document, 10);
            var returned = RequestIdentity(opened.Document, 12);
            True(ReferenceEquals(request.Model, returned.Model), "The tab should retain its actual model.");
            Equal(request.Version, returned.Version);
            Equal(false, request.Matches(returned));
            Equal(true, request.Matches(RequestIdentity(opened.Document, 10)));
        });

        await Run("request identity rejects text URI and language changes independently", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("text", "identity.txt");
            var doc = opened.Document;
            var request = RequestIdentity(doc, 1);
            doc.Model.Replace(0, 1, "T");
            var edited = RequestIdentity(doc, 1);
            Equal(false, request.Matches(edited));
            Equal(true, request.Matches(edited, checkVersion: false));
            var beforeUri = RequestIdentity(doc, 1);
            doc.SetProvider(new MemoryDocumentProvider(doc.Model.Text, "renamed.txt"));
            Equal(beforeUri.Version, doc.Model.VersionId);
            Equal(false, beforeUri.Matches(RequestIdentity(doc, 1), checkVersion: false));
            var beforeLanguage = RequestIdentity(doc, 1);
            doc.Model.LanguageId = "csharp";
            Equal(false, beforeLanguage.Matches(RequestIdentity(doc, 1), checkVersion: false));
        });

        await Run("selection-sensitive results reject caret moves while popup commands remain document-bound", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("abcdef", "selection.txt");
            var request = RequestIdentity(opened.Document, 1, caret: new(1, 2));
            var moved = RequestIdentity(opened.Document, 1, caret: new(1, 4));
            Equal(false, request.Matches(moved, checkSelection: true));
            Equal(true, request.Matches(moved));
            var reversed = RequestIdentity(opened.Document, 1, anchor: new(1, 2), caret: new(1, 1));
            Equal(false, request.Matches(reversed, checkSelection: true));
        });

        await Run("split views share text and undo without sharing view state", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("source", "split.txt");
            var source = opened.Document;
            source.Anchor = new(1, 2);
            source.SecondarySelections.Add((new(1, 1), new(1, 3)));
            var split = source.CreateLinkedView();
            try
            {
                True(source.SharesBuffer(split), "Split views must share a buffer.");
                True(!ReferenceEquals(source.ViewModel, split.ViewModel), "View projections must remain independent.");
                Equal(2, source.ViewCount);
                Equal(source.Anchor, split.Anchor);
                split.Anchor = new(1, 4);
                split.SecondarySelections.Clear();
                Equal(new MonacoPosition(1, 2), source.Anchor);
                Equal(1, source.SecondarySelections.Count);
                source.Model.Replace(0, 6, "changed");
                Equal("changed", split.Model.Text);
                Equal(true, source.IsDirty);
                Equal(true, split.IsDirty);
                True(split.Model.Undo(), "Undo is shared by both views.");
                Equal("source", source.Model.Text);
                Equal(false, split.IsDirty);
            }
            finally { split.Close(); }
            Equal(1, source.ViewCount);
            Equal("source", source.Model.Text);
        });

        await Run("closing the original view does not dispose a surviving split", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("keep", "keep.txt");
            var split = opened.Document.CreateLinkedView();
            opened.Document.Close();
            Equal("keep", split.Model.Text);
            split.Model.Replace(0, 4, "alive");
            Equal("alive", split.Model.Text);
            split.Close();
            await Throws<ObjectDisposedException>(() => { _ = split.Model.Text; return Task.CompletedTask; });
        });

        await Run("save state and save-as identity propagate to every split", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("one", "before.txt");
            var split = opened.Document.CreateLinkedView();
            try
            {
                True(ReferenceEquals(opened.Document.SaveGate, split.SaveGate), "Writes must use a shared save gate.");
                opened.Document.Model.Replace(0, 3, "two");
                split.MarkSaved("two");
                Equal(false, opened.Document.IsDirty);
                Equal(false, split.IsDirty);
                var destination = new MemoryDocumentProvider("two", "after.txt");
                split.SetProvider(destination);
                Equal(destination.Uri, opened.Document.Uri);
                Equal(destination.Uri, split.Uri);
                Equal("after.txt", opened.Document.DisplayName);
            }
            finally { split.Close(); }
        });

        await Run("sticky and preview state belong to a view rather than its buffer", async () =>
        {
            using var opened = await OpenDocument.MemoryAsync("same", "preview.txt");
            var source = opened.Document;
            var split = source.CreateLinkedView();
            try
            {
                source.SetPreview(true);
                source.SetPinned(true);
                Equal(true, source.IsPinned);
                Equal(false, source.IsPreview);
                Equal(false, split.IsPinned);
                split.SetPreview(true);
                source.Model.Replace(0, 4, "edited");
                Equal(false, split.IsPreview);
                Equal(true, split.IsDirty);
            }
            finally { split.Close(); }
        });

        Console.WriteLine($"Native document model tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }
}
