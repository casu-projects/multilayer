using System.Text;

namespace CasuMpOrchestrator;

/// <summary>대화형 콘솔 입출력 중앙화 (구 오케스트레이터 ConsoleIO 포팅) — 백그라운드 로그
/// 줄이 입력 중인 줄을 침범하지 않도록 입력줄을 지우고 로그를 출력한 뒤 재렌더하고,
/// ↑/↓로 명령 히스토리를 호출한다. stdin/stdout이 리다이렉트되면(파이프/테스트) 일반
/// WriteLine으로 폴백한다.
/// 모든 stdout 출력은 이 클래스(또는 타임스탬프 래퍼 경유)를 거쳐야 하며, 입력줄/ANSI
/// 시퀀스는 래퍼에 감싸이기 전의 원본 writer(rawOut)에 직접 쓴다 — 래퍼의 부분 쓰기
/// 버퍼링을 우회해 즉시 반영되고 래퍼와의 재귀도 차단된다.</summary>
internal static class ConsoleIO
{
    /// <summary>대화형 여부 — stdin/stdout이 모두 TTY일 때만 입력 편집/보호가 동작.</summary>
    internal static readonly bool Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>입력줄 프롬프트 (예: "> help").</summary>
    private const string Prompt = "> ";

    private static readonly object Lock = new();
    private static readonly StringBuilder Buffer = new();
    private static int _cursorPos;
    private static bool _lineActive;

    private static readonly List<string> History = new();
    private const int MaxHistory = 200;
    private static int _historyIndex = -1;

    /// <summary>타임스탬프 래퍼에 감싸지기 전의 원본 stdout (SetOut 전에 캡처).</summary>
    private static TextWriter? _rawOut;

    /// <summary>원본 stdout 캡처 (Program.Main — SetOut 전에 호출).</summary>
    internal static void Init(TextWriter rawOut) => _rawOut = rawOut;

    /// <summary>유일한 로그 출력 진입점 (Console.WriteLine 대체): 입력줄을 지우고 로그를
    /// 출력한 뒤 프롬프트+버퍼를 재렌더하고 커서를 복원한다.</summary>
    internal static void WriteLine(string message)
    {
        if (!Interactive)
        {
            _rawOut?.WriteLine(message);
            return;
        }

        lock (Lock)
        {
            if (_lineActive)
            {
                _rawOut?.Write("\r\x1b[2K");
            }

            _rawOut?.WriteLine(message);

            if (_lineActive)
            {
                _rawOut?.Write(Prompt + Buffer);
                int back = Buffer.Length - _cursorPos;
                if (back > 0)
                {
                    _rawOut?.Write($"\x1b[{back}D");
                }
            }
        }
    }

    /// <summary>대화형 stdin 리더 시작 시 1회 호출 — 프롬프트 표시.</summary>
    internal static void ShowPrompt()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            _lineActive = true;
            _rawOut?.Write(Prompt);
        }
    }

    /// <summary>줄 단위 에디터: 에코/백스페이스/삭제/커서 이동을 처리하고 Enter 시 완성된
    /// 라인을 반환, 그 외 null.</summary>
    internal static string? HandleKey(ConsoleKeyInfo key)
    {
        if (!_lineActive) return null;

        lock (Lock)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    _rawOut?.Write('\n');
                    string result = Buffer.ToString();
                    if (!string.IsNullOrEmpty(result))
                    {
                        History.Add(result);
                        if (History.Count > MaxHistory)
                            History.RemoveAt(0);
                    }
                    _historyIndex = -1;
                    Buffer.Clear();
                    _cursorPos = 0;
                    _lineActive = true;
                    _rawOut?.Write(Prompt);
                    return result;
                }

                case ConsoleKey.UpArrow:
                    if (History.Count == 0) return null;
                    if (_historyIndex == -1)
                    {
                        _historyIndex = History.Count - 1;
                    }
                    else if (_historyIndex > 0)
                    {
                        _historyIndex--;
                    }
                    else
                    {
                        return null;
                    }
                    LoadHistory();
                    return null;

                case ConsoleKey.DownArrow:
                    if (_historyIndex < 0) return null;
                    _historyIndex++;
                    if (_historyIndex >= History.Count)
                    {
                        _historyIndex = -1;
                        Buffer.Clear();
                        _cursorPos = 0;
                        Redraw();
                    }
                    else
                    {
                        LoadHistory();
                    }
                    return null;

                case ConsoleKey.Backspace:
                    if (_cursorPos > 0)
                    {
                        Buffer.Remove(_cursorPos - 1, 1);
                        _cursorPos--;
                        Redraw();
                    }
                    return null;

                case ConsoleKey.Delete:
                    if (_cursorPos < Buffer.Length)
                    {
                        Buffer.Remove(_cursorPos, 1);
                        Redraw();
                    }
                    return null;

                case ConsoleKey.LeftArrow:
                    if (_cursorPos > 0)
                    {
                        _cursorPos--;
                        _rawOut?.Write("\x1b[1D");
                    }
                    return null;

                case ConsoleKey.RightArrow:
                    if (_cursorPos < Buffer.Length)
                    {
                        _cursorPos++;
                        _rawOut?.Write("\x1b[1C");
                    }
                    return null;

                case ConsoleKey.Home:
                    if (_cursorPos > 0)
                    {
                        _rawOut?.Write($"\x1b[{_cursorPos}D");
                        _cursorPos = 0;
                    }
                    return null;

                case ConsoleKey.End:
                    if (_cursorPos < Buffer.Length)
                    {
                        _rawOut?.Write($"\x1b[{Buffer.Length - _cursorPos}C");
                        _cursorPos = Buffer.Length;
                    }
                    return null;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        Buffer.Insert(_cursorPos, key.KeyChar);
                        _cursorPos++;
                        Redraw();
                    }
                    return null;
            }
        }
    }

    /// <summary>전체 줄을 지우고 재렌더한 뒤 커서를 복원한다.</summary>
    private static void Redraw()
    {
        _lineActive = true;
        _rawOut?.Write("\r\x1b[2K");
        _rawOut?.Write(Prompt + Buffer);
        int back = Buffer.Length - _cursorPos;
        if (back > 0)
        {
            _rawOut?.Write($"\x1b[{back}D");
        }
    }

    /// <summary>터미널 화면 지우기 (대화형 전용 — 리다이렉트 환경은 no-op).
    /// 입력줄을 보호하며 ANSI clear screen 후 프롬프트를 재렌더한다.</summary>
    internal static void ClearScreen()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            _rawOut?.Write("\x1b[2J\x1b[H");
            if (_lineActive)
            {
                _rawOut?.Write(Prompt + Buffer);
                int back = Buffer.Length - _cursorPos;
                if (back > 0)
                {
                    _rawOut?.Write($"\x1b[{back}D");
                }
            }
        }
    }

    /// <summary>종료 시 호출 — ">" 프롬프트를 정리해 종료 메시지가 깔끔하게 표시되게 한다.</summary>
    internal static void DisableInteractive()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            if (_lineActive)
            {
                _rawOut?.Write("\r\x1b[2K");
            }
            _lineActive = false;
            Buffer.Clear();
        }
    }

    /// <summary>히스토리 항목을 버퍼에 로드 후 재렌더.</summary>
    private static void LoadHistory()
    {
        string entry = History[_historyIndex];
        Buffer.Clear();
        Buffer.Append(entry);
        _cursorPos = entry.Length;
        _lineActive = true;
        _rawOut?.Write("\r\x1b[2K");
        _rawOut?.Write(Prompt + entry);
    }
}
