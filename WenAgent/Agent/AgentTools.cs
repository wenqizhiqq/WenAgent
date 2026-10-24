using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WenAgent.Agent;

public sealed record ToolResult(bool Success, string Output);

public static class AgentTools
{
    private const int MaxReadChars = 20_000;
    private const int MaxOutputChars = 8_000;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls", ".xlsm", ".docx", ".doc", ".pdf", ".zip", ".7z", ".rar",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".mp3", ".wav", ".mp4",
        ".exe", ".dll", ".gguf", ".onnx", ".sqlite", ".db", ".pdb"
    };

    private static readonly HashSet<string> SkipSearchDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".svn", ".nuget", "node_modules", "packages",
        "AppData", "Windows", "WindowsApps", "Program Files", "Program Files (x86)",
        "ProgramData", "$Recycle.Bin", "System Volume Information", "Temp", "logs"
    };

    public static async Task<ToolResult> ExecuteAsync(
        string name, string argsJson, string workspace, bool allowCommands, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            JsonElement args = doc.RootElement;

            return name.Trim().ToLowerInvariant() switch
            {
                "list_files" => ListFiles(workspace, GetString(args, "path", "."), ct),
                "read_file" => ReadFile(workspace, GetString(args, "path", ""), ct),
                "write_file" => WriteFile(workspace, GetString(args, "path", ""),
                    GetString(args, "content", ""), ct),
                "find_files" => FindFiles(workspace, GetString(args, "path", "."),
                    GetString(args, "pattern", "*"), GetInt(args, "max_results", 20), ct),
                "run_command" => await RunCommandAsync(workspace, GetString(args, "command", ""),
                    allowCommands, ct),
                _ => new ToolResult(false,
                    $"未知工具：{name}。可用工具：list_files、read_file、write_file、find_files、run_command。")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(false, $"工具执行出错：{ex.Message}");
        }
    }

    private static ToolResult ListFiles(string workspace, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string full = ResolvePath(workspace, path);

        if (!Directory.Exists(full))
            return new ToolResult(false, $"目录不存在：{path}");

        var sb = new StringBuilder();
        sb.AppendLine($"目录：{DisplayPath(workspace, full)}");
        int count = 0;

        foreach (string dir in Directory.EnumerateDirectories(full))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 300)
            {
                sb.AppendLine("…（条目过多，已截断）");
                break;
            }
            sb.AppendLine($"[目录] {Path.GetFileName(dir)}/");
        }

        foreach (string file in Directory.EnumerateFiles(full))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 300)
            {
                sb.AppendLine("…（条目过多，已截断）");
                break;
            }
            var info = new FileInfo(file);
            sb.AppendLine($"[文件] {info.Name}  {FormatSize(info.Length)}");
        }

        if (count == 0) sb.AppendLine("（空目录）");
        return new ToolResult(true, sb.ToString().TrimEnd());
    }

    private static ToolResult FindFiles(string workspace, string path, string pattern,
        int maxResults, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string root = ResolvePath(workspace, string.IsNullOrWhiteSpace(path) ? "." : path);

        if (!Directory.Exists(root))
            return new ToolResult(false, $"目录不存在：{path}");

        if (string.IsNullOrWhiteSpace(pattern)) pattern = "*";
        pattern = pattern.Trim();
        int limit = Math.Clamp(maxResults, 1, 50);

        var results = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        var stopwatch = Stopwatch.StartNew();
        bool timedOut = false;
        int scanned = 0;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        while (queue.Count > 0 && results.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                timedOut = true;
                break;
            }

            (string dir, int depth) = queue.Dequeue();
            if (!visited.Add(dir)) continue;
            scanned++;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, pattern, options))
                {
                    results.Add(Path.GetFullPath(file));
                    if (results.Count >= limit) break;
                }
            }
            catch
            {
            }

            if (depth >= 6) continue;

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir, "*", options))
                {
                    if (SkipSearchDirs.Contains(Path.GetFileName(sub))) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch
            {
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"搜索根目录：{DisplayPath(workspace, root)}");
        sb.AppendLine($"模式：{pattern}，最多 {limit} 条，深度 6，扫描 {scanned} 个目录");

        if (results.Count == 0)
        {
            sb.AppendLine("没有找到匹配的文件。");
        }
        else
        {
            foreach (string result in results)
                sb.AppendLine(DisplayPath(workspace, result));
        }

        if (timedOut) sb.AppendLine("…搜索超过 10 秒，已停止；请缩小搜索目录。");
        if (results.Count >= limit) sb.AppendLine($"…已达到 {limit} 条上限。");

        return new ToolResult(true, sb.ToString().TrimEnd());
    }

    private static ToolResult ReadFile(string workspace, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new ToolResult(false, "缺少参数 path。");

        string full = ResolvePath(workspace, path);
        if (!File.Exists(full))
            return new ToolResult(false, $"文件不存在：{path}");

        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (BinaryExtensions.Contains(ext))
            return new ToolResult(false,
                $"{ext} 是二进制文件，read_file 无法读取。Excel 请用 run_command 执行 Python + openpyxl 脚本；" +
                "Word 用 python-docx；PDF 用 pypdf。");

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[MaxReadChars + 1];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        string text = new(buffer, 0, read);

        if (text.IndexOf('\0') >= 0)
            return new ToolResult(false, $"文件看起来是二进制格式，无法作为文本读取：{path}");

        bool truncated = read > MaxReadChars;
        if (truncated) text = text[..MaxReadChars];

        if (truncated) text += $"\n…（文件较长，只返回前 {MaxReadChars} 个字符）";
        return new ToolResult(true, text);
    }

    private static ToolResult WriteFile(string workspace, string path, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new ToolResult(false, "缺少参数 path。");

        string full = ResolvePath(workspace, path);
        string? dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);

        File.WriteAllText(full, content, new UTF8Encoding(false));
        return new ToolResult(true,
            $"已写入：{DisplayPath(workspace, full)}（{content.Length} 字符，{FormatSize(new FileInfo(full).Length)}）");
    }

    private static async Task<ToolResult> RunCommandAsync(
        string workspace, string command, bool allowCommands, CancellationToken ct)
    {
        if (!allowCommands)
            return new ToolResult(false, "用户已在界面上关闭“允许执行命令”，请不要重试，改用 final 说明。");

        if (string.IsNullOrWhiteSpace(command))
            return new ToolResult(false, "缺少参数 command。");

        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
            "$OutputEncoding=[System.Text.Encoding]::UTF8; " +
            "$env:PYTHONIOENCODING='utf-8'; $env:PYTHONUTF8='1'; " +
            "$ProgressPreference='SilentlyContinue'; " + command);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new ToolResult(false, "无法启动 PowerShell。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            string partial = SafeJoin(stdoutTask, stderrTask);
            if (ct.IsCancellationRequested) throw;

            return new ToolResult(false,
                $"命令超过 60 秒，已终止。\n{Truncate(partial, MaxOutputChars)}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        string output = Join(stdout, stderr);
        bool success = process.ExitCode == 0;

        string body = success
            ? output.Length == 0 ? "（命令没有输出）" : output
            : $"退出码：{process.ExitCode}\n{output}";

        if (output.Length > MaxOutputChars)
            body = Truncate(body, MaxOutputChars);

        return new ToolResult(success, body.Trim());
    }

    private static string SafeJoin(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
        string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        return Join(stdout, stderr);
    }

    private static string Join(string stdout, string stderr)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout)) parts.Add(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) parts.Add("[stderr]\n" + stderr.TrimEnd());
        return string.Join("\n", parts);
    }

    private static string ResolvePath(string workspace, string relative)
    {
        string rel = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim().Trim('"');
        return Path.IsPathRooted(rel)
            ? Path.GetFullPath(rel)
            : Path.GetFullPath(Path.Combine(workspace, rel));
    }

    private static string DisplayPath(string workspace, string full)
    {
        string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);

        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return ".";
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return full[(root.Length + 1)..];
        return full;
    }

    private static string GetString(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int number)
            ? number
            : fallback;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + $"\n…（已截断，原长 {text.Length} 字符）";
}
