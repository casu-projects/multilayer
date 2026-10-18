using System.Text;

namespace CasuMpOrchestrator;

internal static class ConsoleIO
{
    internal static readonly bool Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    private const string Prompt = "> ";

    private static readonly object Lock = new();
    private static readonly StringBuilder Buffer = new();
    private static int _cursorPos;
    private static bool _lineActive;

    private static readonly List<string> History = new();
    private const int MaxHistory = 200;
    private static int _historyIndex = -1;

    private static TextWriter? _rawOut;

    internal static void Init(TextWriter rawOut) => _rawOut = rawOut;

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

    internal static void ShowPrompt()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            _lineActive = true;
            _rawOut?.Write(Prompt);
        }
    }

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
                    string result = Buffer.ToString(); // 입력 완료
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

                case ConsoleKey.UpArrow: // 이전 히스토리
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

                case ConsoleKey.DownArrow: // 다음 히스토리, 끝이면 새 입력
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

                case ConsoleKey.Backspace: // 커서 앞 삭제
                    if (_cursorPos > 0)
                    {
                        Buffer.Remove(_cursorPos - 1, 1);
                        _cursorPos--;
                        Redraw();
                    }
                    return null;

                case ConsoleKey.Delete: // 커서 뒤 삭제
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

    internal static void ClearScreen()
    {
        if (!Interactive) return;

        lock (Lock)
        {
            _rawOut?.Write("\x1b[2J\x1b[H"); // ANSI 화면 지우기
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
