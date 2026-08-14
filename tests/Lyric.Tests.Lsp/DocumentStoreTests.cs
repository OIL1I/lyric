using Lyric.Lsp.Documents;

namespace Lyric.Tests.Lsp;

/// <summary>The overlay of open buffers: what the editor holds, as opposed to what is on disk.
/// </summary>
public sealed class DocumentStoreTests
{
    private static string Uri(string name) =>
        DocumentUri.FromFilePath(Path.Combine(Path.GetTempPath(), name));

    [Fact]
    public void Keeps_the_uri_the_client_sent_unchanged()
    {
        // Diagnostics are addressed with it. A client that gets back a spelling it does not
        // recognise drops them without a word, which looks exactly like a server that found
        // nothing.
        const string asSent = "file:///c%3A/Users/Olivier/a.lyr";
        var store = new DocumentStore();

        var document = store.Set(asSent, 1, "fn main(): int { return 0; }");

        Assert.NotNull(document);
        Assert.Equal(asSent, document.Uri);
    }

    [Fact]
    public void Two_spellings_of_one_uri_are_one_document()
    {
        var store = new DocumentStore();

        store.Set("file:///c%3A/Users/Olivier/a.lyr", 1, "one");
        store.Set("file:///c:/Users/Olivier/a.lyr", 2, "two");

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Refuses_a_uri_that_names_no_file()
    {
        var store = new DocumentStore();

        Assert.Null(store.Set("untitled:Untitled-1", 1, "fn main(): int { return 0; }"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_document_is_current_until_a_newer_version_replaces_it()
    {
        var store = new DocumentStore();
        var first = store.Set(Uri("a.lyr"), 1, "one")!;

        Assert.True(store.IsCurrent(first));

        store.Set(Uri("a.lyr"), 2, "two");

        // The guard the analysis asks before publishing. Without it a slow compile answers about
        // text the user has already replaced, and the offsets address a document that is gone.
        Assert.False(store.IsCurrent(first));
    }

    [Fact]
    public void A_closed_document_is_no_longer_current()
    {
        var store = new DocumentStore();
        var document = store.Set(Uri("a.lyr"), 1, "one")!;

        var removed = store.Remove(document.Uri);

        Assert.NotNull(removed);
        Assert.False(store.IsCurrent(document));
        Assert.Null(store.ByPath(document.Path));
    }

    [Fact]
    public void Closing_something_that_was_never_open_is_not_an_error()
    {
        var store = new DocumentStore();

        Assert.Null(store.Remove(Uri("never-opened.lyr")));
    }
}
