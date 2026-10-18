using System.IO;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CasuMpServer;

internal static class ConsoleRenderer
{
    public static void Write(IRenderable renderable, IReadOnlyCollection<string>? discordLines = null)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);

        foreach (string line in writer.ToString().Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                Log.ConsoleOnly(trimmed);
            }
        }

        if (discordLines == null) return;
        foreach (string line in discordLines)
        {
            Log.Relay(line);
        }
    }
}
