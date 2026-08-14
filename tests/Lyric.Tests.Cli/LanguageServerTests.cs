using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lyric.Core;

namespace Lyric.Tests.Cli;

/// <summary>
/// <c>lyrls</c> as a process, over real pipes.
///
/// <para>Everything else about the server is tested in process in <c>tests/Lyric.Tests.Lsp</c>.
/// What only a process can answer is the part that project cannot reach: that stdout carries
/// nothing but framed messages, that no newline translation happens between the two, and that the
/// standard library is found beside the binary rather than beside a test assembly.</para>
///
/// <para>The framing is written out here by hand rather than borrowed from <c>Lyric.Lsp</c>. An
/// end-to-end test that used the implementation's own reader and writer would agree with a pair
/// that is wrong in the same way, and this is the one test that sees the actual bytes on the
/// actual pipe.</para>
/// </summary>
public sealed class LanguageServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string Faulty =
        "fn main(): int {\n    let x: int = \"not an int\";\n    return 0;\n}\n";

    [Fact]
    public async Task Answers_a_whole_session_over_stdio_and_exits_cleanly()
    {
        using var session = new Session();

        await session.SendAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}");
        var initialize = await session.ReceiveAsync();

        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        Assert.Equal("lyrls",
            initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        await session.SendAsync(@"{""jsonrpc"":""2.0"",""method"":""initialized"",""params"":{}}");

        using var file = Toolchain.Temp(".lyr");
        var uri = new Uri(file.Path).AbsoluteUri;
        var open = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new { uri, languageId = "lyric", version = 1, text = Faulty },
            },
        });
        await session.SendAsync(open);

        var published = await session.ReceiveNotificationAsync("textDocument/publishDiagnostics");
        var diagnostics = published.GetProperty("params").GetProperty("diagnostics");

        // The same answer 'lyrc check' gives for the same text, from a server that found the
        // standard library on its own.
        var diagnostic = Assert.Single(diagnostics.EnumerateArray().ToList());
        Assert.Equal("LYR-SEM0001", diagnostic.GetProperty("code").GetString());
        Assert.Equal(1, diagnostic.GetProperty("range").GetProperty("start")
            .GetProperty("line").GetInt32());

        await session.SendAsync(@"{""jsonrpc"":""2.0"",""id"":2,""method"":""shutdown""}");
        var shutdown = await session.ReceiveResponseAsync(2);
        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);

        await session.SendAsync(@"{""jsonrpc"":""2.0"",""method"":""exit""}");

        Assert.Equal(ExitCodes.Success, await session.ExitCodeAsync());
    }

    [Fact]
    public async Task Leaving_without_a_shutdown_is_reported_as_a_failure()
    {
        // The distinction a supervisor sees. An editor that closed politely and one that crashed
        // must not leave the same trace.
        using var session = new Session();

        await session.SendAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}");
        await session.ReceiveAsync();
        await session.SendAsync(@"{""jsonrpc"":""2.0"",""method"":""exit""}");

        Assert.Equal(ExitCodes.Failure, await session.ExitCodeAsync());
    }

    [Fact]
    public void The_usage_text_goes_to_stderr_and_stdout_stays_empty()
    {
        // stdout belongs to the protocol for the whole lifetime of the process. One line of help on
        // it would land between two framed messages and desynchronise the stream permanently.
        var result = Toolchain.Run(Toolchain.LyrlsPath, "--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Contains("Language Server Protocol", result.Err);
    }

    [Fact]
    public void An_unknown_argument_is_a_usage_error()
    {
        var result = Toolchain.Run(Toolchain.LyrlsPath, "--serve-http");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
    }

    [Fact]
    public void The_transport_flag_a_language_client_appends_is_accepted()
    {
        // A client that launches a server as a plain executable appends the transport it chose.
        // For stdio that is '--stdio', and it arrives whether the extension asks for it or not:
        // 'args' in the launch options is what the CLIENT adds to, not what it passes verbatim.
        //
        // Rejecting it does not degrade the server, it prevents it from ever starting — and the
        // failure surfaces at the client as a broken pipe during initialize, which says nothing
        // about an argument. Pinned here because the flag is invisible in this repository: it is
        // named in no file we own.
        var result = Toolchain.Run(Toolchain.LyrlsPath, "--stdio", "--version");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(ToolchainVersion.Value, result.Err);
    }

    [Theory]
    [InlineData("--pipe=lyric-42")]
    [InlineData("--socket=5007")]
    public void A_transport_this_server_does_not_have_says_so(string flag)
    {
        // Its own message rather than 'unknown argument'. The caller asked for something specific
        // and is told that specific thing is missing, instead of being pointed at a typo.
        var result = Toolchain.Run(Toolchain.LyrlsPath, flag);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("stdio only", result.Err);
    }

    /// <summary>A running <c>lyrls</c> with framed access to its two pipes.</summary>
    private sealed class Session : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _input;
        private readonly Stream _output;

        public Session()
        {
            var info = new ProcessStartInfo(Toolchain.LyrlsPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Toolchain.RepositoryRoot,
            };

            // Exactly what a language client launching a plain executable passes. Starting the
            // server any other way tests an invocation that never happens.
            info.ArgumentList.Add("--stdio");

            // Zero, so the test does not wait out a quiet period it has no reason to observe.
            info.ArgumentList.Add("--debounce");
            info.ArgumentList.Add("0");

            _process = Process.Start(info)!;

            // The BASE streams. A StreamWriter would apply an encoding and, on Windows, translate
            // the '\r\n' of a header into '\r\r\n'.
            _input = _process.StandardInput.BaseStream;
            _output = _process.StandardOutput.BaseStream;
        }

        public async Task SendAsync(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _input.WriteAsync(header);
            await _input.WriteAsync(body);
            await _input.FlushAsync();
        }

        public async Task<JsonElement> ReceiveAsync()
        {
            var length = -1;
            while (true)
            {
                var line = await ReadLineAsync();
                if (line.Length == 0) break;

                var colon = line.IndexOf(':');
                Assert.True(colon > 0, $"header without a colon: '{line}'");
                if (line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line[(colon + 1)..].Trim());
            }

            Assert.True(length >= 0, "the server sent a header block without a Content-Length");

            var body = new byte[length];
            await _output.ReadExactlyAsync(body).AsTask().WaitAsync(Patience);
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public async Task<JsonElement> ReceiveNotificationAsync(string method)
        {
            while (true)
            {
                var message = await ReceiveAsync();
                if (message.TryGetProperty("method", out var name) && name.GetString() == method)
                    return message;
            }
        }

        public async Task<JsonElement> ReceiveResponseAsync(int id)
        {
            while (true)
            {
                var message = await ReceiveAsync();
                if (message.TryGetProperty("id", out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.GetInt32() == id)
                    return message;
            }
        }

        public async Task<int> ExitCodeAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(Patience);
            return _process.ExitCode;
        }

        /// <summary>One header line, read a byte at a time so nothing of the body is consumed.
        /// </summary>
        private async Task<string> ReadLineAsync()
        {
            var line = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                var read = await _output.ReadAsync(single).AsTask().WaitAsync(Patience);
                Assert.True(read == 1, "the server closed its output inside a header");

                if (single[0] == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.Add(single[0]);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            _process.Dispose();
        }
    }
}
