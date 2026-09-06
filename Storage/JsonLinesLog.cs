using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ExileStats;

// one json object per line, append only; reads a legacy '[...]' array file too, skipping mangled lines
internal static class JsonLinesLog<T>
{
    private static readonly object Lock = new();

    public static void Append(string path, IReadOnlyList<T> items)
    {
        if (items == null || items.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var it in items)
            sb.Append(JsonConvert.SerializeObject(it, Formatting.None)).Append('\n');
        lock (Lock)
        {
            if (File.Exists(path) && FirstChar(path) == '[')
            {
                // legacy array: convert in place first. strict read so a corrupt array throws and
                // aborts here instead of silently overwriting the existing rows with nothing
                File.WriteAllText(path, ToLines(JsonArrayLog<T>.ReadStrict(path)));
            }
            else if (File.Exists(path) && !EndsWithNewline(path))
            {
                // crash mid-append left a torn last line, don't let the new rows merge onto it
                File.AppendAllText(path, "\n");
            }
            File.AppendAllText(path, sb.ToString());
        }
    }

    public static List<T> Read(string path)
    {
        lock (Lock)
        {
            if (!File.Exists(path)) return new List<T>();
            if (FirstChar(path) == '[') return JsonArrayLog<T>.Read(path);
            var list = new List<T>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(JsonConvert.DeserializeObject<T>(line)); } catch { /* mangled or torn line, skip it */ }
            }
            return list;
        }
    }

    private static string ToLines(List<T> items)
    {
        var sb = new StringBuilder();
        foreach (var it in items) sb.Append(JsonConvert.SerializeObject(it, Formatting.None)).Append('\n');
        return sb.ToString();
    }

    private static char FirstChar(string path)
    {
        using var r = new StreamReader(path);
        int c;
        while ((c = r.Read()) >= 0)
            if (!char.IsWhiteSpace((char)c)) return (char)c;
        return '\0';
    }

    private static bool EndsWithNewline(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (fs.Length == 0) return true;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }
}
