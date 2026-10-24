using System.IO;
using LLama.Native;

namespace WenAgent.Agent;

/// <summary>
/// 缓存 llama.cpp 的原生日志，模型加载失败时用于定位原因。
/// </summary>
public static class LlamaLog
{
    private const int MaxLines = 200;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            NativeLogConfig.llama_log_set((level, message) =>
            {
                string line = message.TrimEnd();
                if (line.Length == 0) return;
                lock (Gate)
                {
                    Recent.Enqueue($"[{level}] {line}");
                    while (Recent.Count > MaxLines) Recent.Dequeue();
                }
            });
        }
        catch
        {
            // 日志失败不能影响正常加载
        }
    }

    public static void Clear()
    {
        lock (Gate) Recent.Clear();
    }

    public static string Summary()
    {
        List<string> lines;
        lock (Gate) lines = [.. Recent];

        var picked = lines
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("fail", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("unable", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .TakeLast(8)
            .ToList();

        if (picked.Count == 0) picked = lines.TakeLast(6).ToList();
        return string.Join(Environment.NewLine, picked);
    }

    public static void SaveToFile()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WenAgent");
            Directory.CreateDirectory(dir);

            string content;
            lock (Gate) content = string.Join(Environment.NewLine, Recent);
            File.WriteAllText(Path.Combine(dir, "last-load.log"), content);
        }
        catch
        {
        }
    }
}
