using System.Text;

namespace CasuMpOrchestrator;

// Timestamp 및 송신자를 접두사에 추가한 ConsoleIO.WriteLine 확장 로깅 시스템
public sealed class TimestampedConsoleWriter : TextWriter
{
    public static TimestampedConsoleWriter? Instance { get; private set; }

    public Action<string>? OnConsoleLine;

    private readonly TextWriter _inner;
    private readonly StringBuilder _buffer = new();

    public TimestampedConsoleWriter(TextWriter inner)
    {
        _inner = inner;
        Instance = this;
    }

    public override Encoding Encoding => _inner.Encoding;

    private void EmitLine(string line)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss} orch] {line}";
        OnConsoleLine?.Invoke(formatted);
        ConsoleIO.WriteLine(formatted);
    }

    public void PrintRelayed(string source, string message)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss} {source}] {message}";
        OnConsoleLine?.Invoke(formatted);
        ConsoleIO.WriteLine(formatted);
    }

    private void FlushBuffer()
    {
        if (_buffer.Length == 0) return;
        EmitLine(_buffer.ToString());
        _buffer.Clear();
    }

    public override void Write(char value)
    {
        if (value == '\n')
        {
            FlushBuffer();
        }
        else if (value != '\r')
        {
            _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        int idx = value.IndexOf('\n');
        if (idx < 0)
        {
            _buffer.Append(value);
            return;
        }
        int start = 0;
        while (idx >= 0)
        {
            _buffer.Append(value, start, idx - start);
            FlushBuffer();
            start = idx + 1;
            idx = value.IndexOf('\n', start);
        }
        if (start < value.Length)
        {
            _buffer.Append(value, start, value.Length - start);
        }
    }

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _buffer.Append(value);
        }
        WriteLine();
    }

    public override void WriteLine()
    {
        if (_buffer.Length > 0)
        {
            FlushBuffer();
        }
        else
        {
            EmitLine("");
        }
    }

    public override void Flush() => _inner.Flush();
}
