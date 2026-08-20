using System.Text;

namespace Lyric.Dap;

/// <summary>
/// A <see cref="TextWriter"/> that turns the debuggee's output into per-line callbacks — the
/// adapter forwards each line as an output event, because the adapter's own stdout is the
/// protocol stream and may carry nothing else.
///
/// <para>Buffered by line: a program printing a character at a time would otherwise produce one
/// protocol message per character. <see cref="Flush"/> releases a partial line, which is what
/// the pump calls when the program ends mid-line.</para>
/// </summary>
public sealed class EventTextWriter(Action<string> emit) : TextWriter
{
    private readonly StringBuilder _line = new();
    private readonly Lock _lock = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n')
            {
                var line = _line.ToString().TrimEnd('\r');
                _line.Clear();
                emit(line);
                return;
            }
            _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var c in value) Write(c);
    }

    public override void Flush()
    {
        lock (_lock)
        {
            if (_line.Length == 0) return;
            var line = _line.ToString();
            _line.Clear();
            emit(line);
        }
    }
}
