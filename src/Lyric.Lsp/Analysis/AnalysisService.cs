using System.Collections.Concurrent;
using Lyric.Compiler;
using Lyric.Lsp.Documents;
using Lyric.Lsp.Protocol;

namespace Lyric.Lsp.Analysis;

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

    /// <summary>Compiles one version and publishes its diagnostics, without the debounce. The
    /// analysis a test drives directly.</summary>
    public async Task AnalyzeAsync(OpenDocument document, CancellationToken cancellationToken)
    {
        var options = new CompilerOptions { StdlibRoot = _stdlibRoot };

        // Off the caller's thread: the compile is synchronous and CPU-bound, and the caller is
        // either the read loop or a test.
        var result = await Task.Run(
            () => SourceCompiler.Check(
                ScriptSource.FromBuffer(document.Path, document.Text), options),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Asked AFTER the compile as well as before it. The compile takes long enough for the file
        // to have changed while it ran, and that is precisely when publishing does damage.
        if (!_documents.IsCurrent(document)) return;

        var file = DiagnosticMapper.FindFile(result.Sources, document.Path);
        var diagnostics = file.IsValid
            ? DiagnosticMapper.ForFile(result.Sources, result.Diagnostics.SortedSnapshot(), file)
            : [];

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

        await _publish(new PublishDiagnosticsParams
        {
            Uri = document.Uri,
            Diagnostics = [],
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Logging must not itself throw on a connection that is already going down.</summary>
    private async Task SafeLogAsync(string message)
    {
        try
        {
            await _log(message, MessageType.Error, CancellationToken.None).ConfigureAwait(false);
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
