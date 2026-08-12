using System.Text;

namespace CasuMpOrchestrator;

// 콘솔 출력에 전역 타임스탬프 + 소스 접두사를 부여하는 TextWriter 래퍼: 자기 로그는
// "[HH:mm:ss] [orch] ", 릴레이 로그는 PrintRelayed로 "[HH:mm:ss] [source] ".
// 완성 라인은 ConsoleIO.WriteLine 경유(대화형 입력줄 보호, 원본 writer 사용이라 재귀 없음),
// 부분 쓰기는 개행 단위로 버퍼링해 한 줄당 접두사 하나를 붙인다.
public sealed class TimestampedConsoleWriter : TextWriter
{
 // 전역 인스턴스 - LOG 릴레이 핸들러가 PrintRelayed로 접근한다.
    public static TimestampedConsoleWriter? Instance { get; private set; }

 // 완성된 로그 라인 콜백 (접두사 포함 - Discord 콘솔 릴레이용).
 // EmitLine/PrintRelayed가 표시 전에 호출한다 - 오케스트레이터 자체 로그와
 // 게이트웨이/에이전트/인스턴스 릴레이 로그가 전부 이 경로로 수집된다.
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

 // 릴레이 로그 표시 (agent/gateway/인스턴스 - LOG 메시지 수신 시).
 // 소스는 "name:subname/thirdname" 규약 (예: agent:m1/depth-1) - 단일 블록에 통합.
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
