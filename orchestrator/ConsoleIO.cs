using System.Text;

namespace CasuMpOrchestrator;

// 대화형 콘솔 입출력 중앙화 - 백그라운드 로그가 입력 중인 줄을 침범하지 않도록 지우고
// 출력 후 재렌더하며 ↑/↓ 히스토리를 지원한다. stdin/stdout 리다이렉트 시 일반 WriteLine 폴백
// 모든 stdout은 이 클래스(또는 타임스탬프 래퍼 경유)를 거치며, ANSI/입력줄은 래퍼 이전의
// 원본 writer에 직접 쓴다 (래퍼의 부분 쓰기 버퍼링 우회 + 재귀 차단)
internal static class ConsoleIO
{
    // 대화형 여부 - stdin/stdout이 모두 TTY일 때만 입력 편집/보호가 동작
    internal static readonly bool Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    // 입력줄 프롬프트 (예: "> help")
    private const string Prompt = "> ";

    private static readonly object Lock = new();
    private static readonly StringBuilder Buffer = new();
    private static int _cursorPos;
    private static bool _lineActive;

    private static readonly List<string> History = new();
    private const int MaxHistory = 200;
    private static int _historyIndex = -1;

    // 타임스탬프 래퍼에 감싸지기 전의 원본 stdout (SetOut 전에 캡처)
    private static TextWriter? _rawOut;

    // 원본 stdout 캡처 (Program.Main - SetOut 전에 호출)
    internal static void Init(TextWriter rawOut) => _rawOut = rawOut;

    // 유일한 로그 출력 진입점 (Console.WriteLine 대체): 입력줄을 지우고 로그를
    // 출력한 뒤 프롬프트+버퍼를 재렌더하고 커서를 복원한다
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

    // 대화형 stdin 리더 시작 시 1회 호출 - 프롬프트 표시
    internal static void ShowPrompt()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            _lineActive = true;
            _rawOut?.Write(Prompt);
        }
    }

    // 줄 단위 에디터: 에코/백스페이스/삭제/커서 이동을 처리하고 Enter 시 완성된
    // 라인을 반환, 그 외 null
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

    // 전체 줄을 지우고 재렌더한 뒤 커서를 복원한다
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

    // 터미널 화면 지우기 (대화형 전용 - 리다이렉트 환경은 no-op)
    // 입력줄을 보호하며 ANSI clear screen 후 프롬프트를 재렌더한다
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

    // 종료 시 호출 - ">" 프롬프트를 정리해 종료 메시지가 깔끔하게 표시되게 한다
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

    // 히스토리 항목을 버퍼에 로드 후 재렌더
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
