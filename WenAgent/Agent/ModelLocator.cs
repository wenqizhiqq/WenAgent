using System.Diagnostics;
using System.IO;

namespace WenAgent.Agent;

/// <summary>
/// 模型定位：优先使用约定路径，其次跨盘浅层搜索同名文件，
/// 最后搜索文件名含 qwen 的最大 gguf。
/// </summary>
public static class ModelLocator
{
    public const string PreferredPath = @"D:\wqz\models\qwen2.5-7b-instruct-1m-q4_k_m.gguf";

    public const string DownloadUrl =
        "https://modelscope.cn/models?name=Qwen2.5-4B-Instruct-GGUF&page=1&tabKey=task";

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi",
        "Windows", "WindowsApps", "Program Files", "Program Files (x86)", "ProgramData",
        "Recovery", "PerfLogs", "AppData", "node_modules", ".git", ".vs", ".nuget",
        "packages", "Temp", "obj", "bin"
    };

    private const int MaxDepth = 3;
    private const int MaxDirectoriesPerRoot = 60000;
    private static readonly TimeSpan PerRootBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(10);

    private static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WenAgent", "model-path.txt");

    public static async Task<string?> LocateAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(PreferredPath))
        {
            progress?.Report("已找到指定模型");
            return PreferredPath;
        }

        string? saved = ReadSavedPath();
        if (saved != null && File.Exists(saved))
        {
            progress?.Report("使用上次选择的模型");
            return saved;
        }

        progress?.Report("正在跨盘搜索 GGUF 模型…");
        return await Task.Run(() => Search(progress, ct), ct);
    }

    public static void SavePath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            File.WriteAllText(ConfigFile, path);
        }
        catch
        {
            // 记住路径失败不影响本次使用
        }
    }

    public static bool OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string SearchHint =>
        $"约定路径：{PreferredPath}\n" +
        "也可以把任意 Qwen GGUF 放进 D:\\wqz\\models，或点击“选择本地模型”手动指定。";

    private static string? ReadSavedPath()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            string path = File.ReadAllText(ConfigFile).Trim();
            return path.Length == 0 ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static string? Search(IProgress<string>? progress, CancellationToken ct)
    {
        string fileName = Path.GetFileName(PreferredPath);
        string? found = SearchAllRoots(fileName, exactName: true, progress, ct)
                        ?? SearchAllRoots(fileName, exactName: false, progress, ct);
        return found;
    }

    private static string? SearchAllRoots(string fileName, bool exactName, IProgress<string>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        foreach (string root in GetSearchRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (total.Elapsed >= TotalBudget) break;

            progress?.Report(exactName
                ? $"搜索 {root} 下的同名模型…"
                : $"搜索 {root} 下的 Qwen 模型…");

            string? hit = SearchRoot(root, fileName, exactName, progress, ct);
            if (hit != null) return hit;
        }
        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var roots = new List<string>();

        try
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(baseDir)) roots.Add(baseDir);
        }
        catch
        {
        }

        string systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
            .TrimEnd(Path.DirectorySeparatorChar);

        var drives = new List<DriveInfo>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                drives.Add(drive);
            }
        }
        catch
        {
        }

        // 先查非系统盘，模型通常放在数据盘
        foreach (DriveInfo drive in drives.Where(d => !SameDrive(d.Name, systemDrive)))
            roots.Add(drive.Name);
        foreach (DriveInfo drive in drives.Where(d => SameDrive(d.Name, systemDrive)))
            roots.Add(drive.Name);

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameDrive(string path, string drive)
        => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), drive, StringComparison.OrdinalIgnoreCase);

    private static string? SearchRoot(string root, string fileName, bool exactName,
        IProgress<string>? progress, CancellationToken ct)
    {
        var budget = Stopwatch.StartNew();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && budget.Elapsed < PerRootBudget && visited < MaxDirectoriesPerRoot)
        {
            ct.ThrowIfCancellationRequested();
            (string dir, int depth) = queue.Dequeue();
            visited++;

            if (visited % 400 == 0)
                progress?.Report($"搜索中：{dir}");

            if (exactName)
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            else
            {
                string? best = FindLargestQwenGguf(dir);
                if (best != null) return best;
            }

            if (depth >= MaxDepth) continue;

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if (SkipFolders.Contains(Path.GetFileName(sub))) continue;
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? FindLargestQwenGguf(string dir)
    {
        string? best = null;
        long bestSize = -1;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.gguf"))
            {
                if (Path.GetFileName(file).IndexOf("qwen", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    long size = new FileInfo(file).Length;
                    if (size > bestSize)
                    {
                        bestSize = size;
                        best = file;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return best;
    }
}
