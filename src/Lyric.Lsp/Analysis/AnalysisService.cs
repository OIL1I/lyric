using System.Collections.Concurrent;
using Lyric.Compiler;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

/// <summary>
/// One finished analysis, kept so questions can be answered between compiles.
///
/// <para>They belong together and are useless apart: the model is keyed by nodes whose spans point
/// at <see cref="File"/> in <see cref="Sources"/>, and <see cref="Text"/> is what those offsets
/// count into. A position converted against any other text addresses this tree at the wrong
/// place.</para>
///
/// <para><see cref="Sources"/> is carried rather than reached through the model: a
/// <c>Compilation</c> keeps its source manager to itself, and a span cannot be turned into a line
/// and a column without one.</para>
/// </summary>
public sealed record AnalysisSnapshot(
    Compiler.SemanticModel Model,
    Core.SourceManager Sources,
    Core.FileId File,
    string Text,
    int Version);

/// <summary>
/// Turns buffer changes into published diagnostics.
///
/// <para>An object with state rather than a function, because the state is the point: which
/// analysis is pending for which document, and whether the answer that just came back is still
/// about the text the user is looking at.</para>
///
/// <para>The compiler is run WHOLE for every analysis. It has no incremental mode, and building
/// one before the cost is known would be optimising a number nobody has measured in this process.
/// What that costs is dominated by the standard library rather than by the user's file, and it is
/// the same work <c>lyrc check</c> does.</para>
///
/// <para>A run in flight cannot be stopped: <see cref="SourceCompiler"/> takes no cancellation
/// token. Cancellation therefore means the RESULT IS DISCARDED, not that the work stops. For a
/// keystroke-driven server that is the distinction that matters — the wasted milliseconds are
/// invisible, a published stale answer is not.</para>
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private readonly DocumentStore _documents;
    private readonly Func<PublishDiagnosticsParams, CancellationToken, Task> _publish;
    private readonly Func<string, MessageType, CancellationToken, Task> _log;
    private readonly TimeSpan _debounce;
    private readonly string? _stdlibRoot;

    /// <summary>The pending analysis per document path. Replacing an entry cancels the run it
    /// stands for.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending =
        new(DocumentUri.PathComparer);

    /// <summary>
    /// What was last said about a project file, by the directory the search started in.
    ///
    /// <para>The project file is read on every analysis — it is one small file and the compile
    /// beside it costs milliseconds — but SAYING something about it belongs to a change. Logging a
    /// warning per keystroke would bury the one line that matters under the same line.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _projectNotices =
        new(DocumentUri.PathComparer);

    /// <summary>
    /// Which files the last analysis of a document READ, by document path.
    ///
    /// <para>Taken from the compilation itself rather than from the imports in the text: the
    /// resolver already followed them, transitively and through the project file's roots, and a
    /// second answer to "what does this depend on" would be the one that is wrong.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _dependencies =
        new(DocumentUri.PathComparer);

    /// <summary>
    /// The last analysis that produced a model, per document path.
    ///
    /// <para>Kept because a question about the program has to be answerable while the program does
    /// not parse. Half the time a cursor rests somewhere, the buffer is mid-edit; a server that
    /// only answered from the current text would go silent exactly when someone is looking
    /// something up. The answer is then one edit old, which is visible and harmless, as against no
    /// answer, which reads as "there is nothing here".</para>
    ///
    /// <para>The TEXT is kept with it: a position from the client has to be turned into an offset
    /// against the text the model was built from, not against the buffer that has moved on.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, AnalysisSnapshot> _lastGood =
        new(DocumentUri.PathComparer);

    private bool _disposed;

    public AnalysisService(
        DocumentStore documents,
        Func<PublishDiagnosticsParams, CancellationToken, Task> publish,
        Func<string, MessageType, CancellationToken, Task> log,
        TimeSpan debounce,
        string? stdlibRoot = null)
    {
        _documents = documents;
        _publish = publish;
        _log = log;
        _debounce = debounce;
        _stdlibRoot = stdlibRoot;
    }

    /// <summary>
    /// Plans an analysis of this version and returns at once.
    ///
    /// <para>The caller is the read loop, which must not wait: while it waits it reads no
    /// messages, and one of the unread ones is the edit that makes this analysis obsolete.</para>
    /// </summary>
    public void Schedule(OpenDocument document)
    {
        if (_disposed) return;

        var source = new CancellationTokenSource();
        if (_pending.TryRemove(document.Path, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _pending[document.Path] = source;

        _ = RunAsync(document, source);
    }

    private async Task RunAsync(OpenDocument document, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            if (_debounce > TimeSpan.Zero)
                await Task.Delay(_debounce, token).ConfigureAwait(false);

            // The cheapest of the three guards, and the one that catches the common case: the user
            // is still typing, so several versions were superseded before any of them compiled.
            if (!_documents.IsCurrent(document)) return;

            await AnalyzeAsync(document, token).ConfigureAwait(false);
            await AnalyzeDependentsAsync(document, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer version arrived. Nothing to report: the run that replaced this one will.
        }
        catch (Exception exception)
        {
            // A compiler fault must not take the connection down with it. The user keeps the
            // diagnostics of the last run, which are stale but were true of a real state of the
            // file, and the reason lands in the client's log rather than nowhere.
            await SafeLogAsync(
                $"analysis of {document.Path} failed: {exception.GetType().Name}: {exception.Message}")
                .ConfigureAwait(false);
        }
        finally
        {
            if (_pending.TryGetValue(document.Path, out var current) && current == source)
                _pending.TryRemove(document.Path, out _);
            source.Dispose();
        }
    }

    /// <summary>
    /// The <c>lyric.json</c> above this document, or <c>null</c> when there is none or it cannot be
    /// understood.
    ///
    /// <para>A BROKEN FILE DOES NOT STOP THE ANALYSIS. Falling back to the plain rules gives
    /// resolution that may be wrong, and the log says why; publishing nothing would leave the
    /// editor showing the diagnostics of some earlier state with no hint that anything happened.
    /// For a tool someone is looking at, wrong-and-explained beats silent.</para>
    ///
    /// <para>Not published as a diagnostic on this document: the fault is in another file, and
    /// pointing at the wrong place is how a reader loses an afternoon. The log is where a message
    /// about a file the client did not ask about belongs.</para>
    /// </summary>
    private async Task<ProjectFile?> ProjectForAsync(OpenDocument document)
    {
        var directory = Path.GetDirectoryName(document.Path);
        if (directory is null) return null;

        try
        {
            var project = ProjectFile.Discover(directory);

            var notice = project is null || project.Warnings.Count == 0
                ? string.Empty
                : $"{Path.Combine(project.Directory, ProjectFile.FileName)}: "
                  + string.Join("; ", project.Warnings);

            await NoticeAsync(directory, notice, MessageType.Warning).ConfigureAwait(false);
            return project;
        }
        catch (ProjectFileException broken)
        {
            await NoticeAsync(directory, $"{broken.Path}: {broken.Message}", MessageType.Error)
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Re-analyses every open document whose last compilation read this one.
    ///
    /// <para>ONE LEVEL ONLY: a cascaded run does not cascade again. Two modules may import each
    /// other — that is a diagnostic rather than a crash, so both still compile — and a transitive
    /// cascade over such a pair would not terminate. One level is also all that is needed: a change
    /// reaches every open file that reads the changed one, and a file that reads THAT one was
    /// itself re-read in the process.</para>
    /// </summary>
    private async Task AnalyzeDependentsAsync(OpenDocument changed, CancellationToken token)
    {
        string full;
        try { full = Path.GetFullPath(changed.Path); }
        catch (ArgumentException) { return; }

        foreach (var other in _documents.All)
        {
            if (DocumentUri.PathComparer.Equals(other.Path, changed.Path)) continue;
            if (!_dependencies.TryGetValue(other.Path, out var reads)) continue;
            if (!reads.Contains(full)) continue;

            token.ThrowIfCancellationRequested();
            await AnalyzeAsync(other, token).ConfigureAwait(false);
        }
    }

    /// <summary>Every file a compilation put in its source manager, as absolute paths. A virtual
    /// name that is not a path is dropped rather than guessed at.</summary>
    private static HashSet<string> FilesRead(Core.SourceManager sources)
    {
        var files = new HashSet<string>(DocumentUri.PathComparer);

        for (var i = 1; i <= sources.FileCount; i++)
        {
            try { files.Add(Path.GetFullPath(sources.GetPath(new Core.FileId(i)))); }
            catch (ArgumentException) { /* not a path; it can match no document */ }
        }

        return files;
    }

    /// <summary>Says something about a project file only when it differs from what was last said
    /// about it.</summary>
    private async Task NoticeAsync(string directory, string message, MessageType type)
    {
        if (_projectNotices.TryGetValue(directory, out var previous)
            && string.Equals(previous, message, StringComparison.Ordinal))
            return;

        _projectNotices[directory] = message;
        if (message.Length > 0) await SafeLogAsync(message, type).ConfigureAwait(false);
    }

    /// <summary>
    /// How this document is compiled: where the standard library is, what its project file says, and
    /// the text of everything else the editor holds.
    ///
    /// <para>One place, because the analysis and the completion have to compile the same program.
    /// Two option sets that differ in the project root would answer two different questions about
    /// one file, and only one of them would be on screen.</para>
    /// </summary>
    private async Task<CompilerOptions> OptionsForAsync(OpenDocument document)
    {
        var project = await ProjectForAsync(document).ConfigureAwait(false);

        return new CompilerOptions
        {
            StdlibRoot = _stdlibRoot,
            SourceRoot = project?.SourceRoot,
            NativeRoots = project?.NativeRoots,

            // Everything the editor holds, not only this buffer. Without it a program is checked
            // against its own unsaved text and against the last SAVE of every module it imports,
            // and the two disagree for as long as an edit is unsaved.
            SourceOverlay = _documents.Overlay(),
        };
    }

    /// <summary>
    /// The completions at an offset in a document's CURRENT text.
    ///
    /// <para>Not from the last good analysis: that model was built from the text before the
    /// keystroke that triggered this, and its spans are indices into that text. A compile of its own
    /// is what makes the answer about what the user is looking at.</para>
    /// </summary>
    public async Task<IReadOnlyList<CompletionItem>?> CompleteAsync(
        OpenDocument document, int offset, CancellationToken cancellationToken)
    {
        var options = await OptionsForAsync(document).ConfigureAwait(false);

        return await Task.Run(
            () => CompletionProvider.At(document.Path, document.Text, offset, options),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles one version and publishes its diagnostics, without the debounce. The
    /// analysis a test drives directly.</summary>
    public async Task AnalyzeAsync(OpenDocument document, CancellationToken cancellationToken)
    {
        var options = await OptionsForAsync(document).ConfigureAwait(false);

        // Off the caller's thread: the compile is synchronous and CPU-bound, and the caller is
        // either the read loop or a test.
        var result = await Task.Run(
            () => SourceCompiler.Check(
                ScriptSource.FromBuffer(document.Path, document.Text), options),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Recorded even when the result is discarded below: what this document reads does not
        // depend on whether its diagnostics are still wanted.
        _dependencies[document.Path] = FilesRead(result.Sources);

        // Asked AFTER the compile as well as before it. The compile takes long enough for the file
        // to have changed while it ran, and that is precisely when publishing does damage.
        if (!_documents.IsCurrent(document)) return;

        var file = DiagnosticMapper.FindFile(result.Sources, document.Path);
        var diagnostics = file.IsValid
            ? DiagnosticMapper.ForFile(result.Sources, result.Diagnostics.SortedSnapshot(), file)
            : [];

        // Errors do not disqualify a model — a program with a type error still has resolved names
        // and types for everything around it, which is the state an editor is in most of the time.
        if (file.IsValid && result.Model is { } model)
        {
            _lastGood[document.Path] =
                new AnalysisSnapshot(model, result.Sources, file, document.Text, document.Version);
        }

        // The entry file is missing from the source manager only when the run never opened it. The
        // diagnostic explaining why is in the result and is addressed to no file, so it would be
        // dropped by the filter and the editor would show a clean document that does not compile.
        if (!file.IsValid)
        {
            foreach (var diagnostic in result.Diagnostics.SortedSnapshot())
                await SafeLogAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        await _publish(new PublishDiagnosticsParams
        {
            Uri = document.Uri,
            Version = document.Version,
            Diagnostics = diagnostics,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Withdraws the diagnostics of a document and cancels anything pending for it.
    ///
    /// <para>An empty list rather than no message. Diagnostics are state the server owns in the
    /// client, and a server that simply stops talking about a file leaves its last squiggles in
    /// the editor for as long as the session lasts.</para>
    /// </summary>
    public async Task ClearAsync(OpenDocument document, CancellationToken cancellationToken)
    {
        if (_pending.TryRemove(document.Path, out var pending))
        {
            pending.Cancel();
            pending.Dispose();
        }

        // The model goes with the buffer. Keeping it would answer questions about a file the editor
        // has closed, from text it no longer holds.
        _lastGood.TryRemove(document.Path, out _);

        await _publish(new PublishDiagnosticsParams
        {
            Uri = document.Uri,
            Diagnostics = [],
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The newest analysis of this document that produced a model, or <c>null</c> when none ever
    /// did — a file whose very first version failed to open.
    /// </summary>
    public AnalysisSnapshot? LastGood(string path) =>
        _lastGood.TryGetValue(path, out var snapshot) ? snapshot : null;

    /// <summary>Logging must not itself throw on a connection that is already going down.</summary>
    private async Task SafeLogAsync(string message, MessageType type = MessageType.Error)
    {
        try
        {
            await _log(message, type, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The client is gone. There is nowhere left to report this.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }
        _pending.Clear();
    }
}
