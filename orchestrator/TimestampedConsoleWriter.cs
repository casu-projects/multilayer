using System.Text;

namespace CasuMpOrchestrator;

/// <summary>콘솔 출력에 전역 타임스탬프 + 소스 접두사를 부여하는 TextWriter 래퍼.
/// 자기 로그(일반 WriteLine)는 "[HH:mm:ss] [orch] " — 릴레이 로그는 PrintRelayed를 통해
/// "[HH:mm:ss] [source] "로 표시된다 (접두사는 오케스트레이터 표시 계층이 단독 부여).
/// 완성된 라인은 ConsoleIO.WriteLine으로 라우팅 — 대화형 시 입력줄 보호(로그 침범 방지)를
/// 받고, ConsoleIO는 SetOut 전 원본 writer를 직접 사용하므로 재귀가 없다.
/// 부분 쓰기(Write)는 개행 단위로 버퍼링해 한 줄당 하나의 접두사를 붙인다.</summary>
public sealed class TimestampedConsoleWriter : TextWriter
{
    /// <summary>전역 인스턴스 — LOG 릴레이 핸들러가 PrintRelayed로 접근한다.</summary>
    public static TimestampedConsoleWriter? Instance { get; private set; }

    private readonly TextWriter _inner;
    private readonly StringBuilder _buffer = new();

    public TimestampedConsoleWriter(TextWriter inner)
    {
        _inner = inner;
        Instance = this;
    }

    public override Encoding Encoding => _inner.Encoding;

    private void EmitLine(string line) =>
        ConsoleIO.WriteLine($"[{DateTime.Now:HH:mm:ss} orch] {line}");

    /// <summary>릴레이 로그 표시 (agent/gateway/인스턴스 — LOG 메시지 수신 시).
    /// 소스는 "name:subname/thirdname" 규약 (예: agent:m1/depth-1) — 단일 블록에 통합.</summary>
    public void PrintRelayed(string source, string message) =>
        ConsoleIO.WriteLine($"[{DateTime.Now:HH:mm:ss} {source}] {message}");

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
