using System.Collections.Generic;
using System.Text;

namespace CasuMp.Protocol;

public static class CommandLineTokenizer
{
    public static string[] Split(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(input)) return result.ToArray();

        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result.ToArray();
    }
}
