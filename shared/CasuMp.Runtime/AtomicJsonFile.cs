using System.IO;

namespace CasuMp.Runtime;

public static class AtomicJsonFile
{
    public static void Write(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    public static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
