using System.Text;

namespace Lyric.Core;

/// <summary>
/// The encoding of a tool's own standard streams.
///
/// <para>On Windows a REDIRECTED stream is still encoded with the console's code page, and that
/// code page is process-shared state: whichever program attached to the console changes it changes
/// what every other one writes. A best-fit mapping there is silent and lossy — an em dash leaves as
/// a hyphen — so two tools started at different moments could disagree about the same text.</para>
///
/// <para>Redirected output is not going to a console at all, so it is written as UTF-8: one
/// encoding, decided by the writer, independent of whichever console the process happens to be
/// attached to. Redirected input is read the same way, so a pipe is UTF-8 from end to end.</para>
///
/// <para>An UNREDIRECTED stream is left untouched. There the console's code page is the right
/// answer, and setting it is the very mutation this exists to avoid.</para>
/// </summary>
public static class ConsoleStreams
{
    /// <summary>No byte-order mark: these bytes go into a pipe, where a BOM is content.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The first statement of every tool's <c>Main</c>, before anything is written.</summary>
    public static void UseUtf8WhenRedirected()
    {
        // AutoFlush as on the writer being replaced: a tool's output is read by another process,
        // which must not wait for a buffer to fill.
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true });

        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true });

        if (Console.IsInputRedirected)
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Utf8));
    }
}
