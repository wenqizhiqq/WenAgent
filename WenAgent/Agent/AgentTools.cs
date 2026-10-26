using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WenAgent.Agent;

public sealed record ToolResult(bool Success, string Output);

public static class AgentTools
{
    private const int MaxReadChars = 20_000;
    private const int MaxOutputChars = 8_000;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private static readonly HttpClient Http = CreateHttp(TimeSpan.FromSeconds(30));
    private static readonly HttpClient DownloadHttp = CreateHttp(Timeout.InfiniteTimeSpan);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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
                "write_file" => await WriteFileAsync(workspace, GetString(args, "path", ""),
                    GetString(args, "content", ""), ct),
                "find_files" => FindFiles(workspace, GetString(args, "path", "."),
                    GetString(args, "pattern", "*"), GetInt(args, "max_results", 20), ct),
                "search_text" => SearchText(workspace, GetString(args, "path", "."),
                    GetString(args, "query", ""), GetString(args, "pattern", "*"),
                    GetBool(args, "regex", false), GetInt(args, "max_results", 50), ct),
                "replace_in_file" => await ReplaceInFileAsync(workspace, args, ct),
                "fetch_url" => await FetchUrlAsync(GetString(args, "url", ""),
                    GetInt(args, "max_chars", 12000), ct),
                "system_info" => GetSystemInfo(),
                "capture_screen" => CaptureScreen(workspace, GetString(args, "path", ""), ct),
                "run_command" => await RunCommandAsync(workspace, GetString(args, "command", ""),
                    allowCommands, ct),
                "run_python" => await RunPythonAsync(workspace, GetString(args, "script", ""),
                    allowCommands, ct),
                _ => new ToolResult(false,
                    $"未知工具：{name}。可用工具：list_files、read_file、write_file、find_files、search_text、replace_in_file、set_workspace、run_python、fetch_url、download_file、capture_screen、system_info、inspect_image、run_command。")
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

    private static ToolResult SearchText(
        string workspace, string path, string query, string pattern, bool regex, int maxResults,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(false, "缺少参数 query。");

        string root = ResolvePath(workspace, string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(root))
            return new ToolResult(false, $"目录不存在：{path}");

        int limit = Math.Clamp(maxResults, 1, 100);
        pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();

        Regex? matcher = null;
        if (regex)
        {
            try
            {
                matcher = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch (Exception ex)
            {
                return new ToolResult(false, "正则表达式无效：" + ex.Message);
            }
        }

        var results = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        var stopwatch = Stopwatch.StartNew();
        int scannedDirs = 0, searchedFiles = 0;
        bool timedOut = false;

        while (queue.Count > 0 && results.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
            {
                timedOut = true;
                break;
            }

            (string dir, int depth) = queue.Dequeue();
            if (!visited.Add(dir)) continue;
            scannedDirs++;

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, pattern, options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (results.Count >= limit) break;

                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (BinaryExtensions.Contains(ext)) continue;

                    var info = new FileInfo(file);
                    if (info.Length > 2_000_000) continue;

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file, Encoding.UTF8);
                    }
                    catch
                    {
                        continue;
                    }

                    searchedFiles++;

                    for (int i = 0; i < lines.Length && results.Count < limit; i++)
                    {
                        string line = lines[i];
                        bool hit = matcher != null
                            ? matcher.IsMatch(line)
                            : line.Contains(query, StringComparison.OrdinalIgnoreCase);

                        if (!hit) continue;

                        string text = line.Trim();
                        if (text.Length > 300) text = text[..300] + "…";
                        results.Add($"{DisplayPath(workspace, file)}:{i + 1}: {text}");
                    }
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
        sb.AppendLine($"搜索：{query}");
        sb.AppendLine($"根目录：{DisplayPath(workspace, root)}  模式：{pattern}  扫描：{scannedDirs} 目录 / {searchedFiles} 文件");

        if (results.Count == 0)
            sb.AppendLine("没有找到匹配内容。");
        else
            foreach (string result in results) sb.AppendLine(result);

        if (timedOut) sb.AppendLine("…搜索超过 15 秒，已停止；请缩小目录或指定 pattern。");
        if (results.Count >= limit) sb.AppendLine($"…已达到 {limit} 条上限。");

        return new ToolResult(true, sb.ToString().TrimEnd());
    }

    private static async Task<ToolResult> ReplaceInFileAsync(
        string workspace, JsonElement args, CancellationToken ct)
    {
        string path = GetString(args, "path", "");
        string oldText = GetString(args, "old", "");
        if (oldText.Length == 0) oldText = GetString(args, "find", "");

        string newText = args.TryGetProperty("new", out JsonElement newValue)
                         && newValue.ValueKind == JsonValueKind.String
            ? newValue.GetString() ?? ""
            : GetString(args, "replace", "");

        int requestedCount = GetInt(args, "count", 0);

        if (string.IsNullOrWhiteSpace(path)) return new ToolResult(false, "缺少参数 path。");
        if (oldText.Length == 0) return new ToolResult(false, "缺少参数 old（要替换的原文）。");

        string full = ResolvePath(workspace, path);
        if (!File.Exists(full)) return new ToolResult(false, $"文件不存在：{path}");

        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (BinaryExtensions.Contains(ext))
            return new ToolResult(false,
                $"{ext} 是二进制/Office 文件，不能用 replace_in_file；请用 Python 库修改。");

        var info = new FileInfo(full);
        if (info.Length > 2_000_000)
            return new ToolResult(false, "文件太大（超过 2MB），请用脚本处理。");

        byte[] bytes = File.ReadAllBytes(full);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));

        int occurrences = CountOccurrences(text, oldText);
        if (occurrences == 0)
            return new ToolResult(false, $"在文件中没有找到要替换的内容：{path}");

        int toReplace = requestedCount > 0 ? Math.Min(requestedCount, occurrences) : occurrences;
        string updated = toReplace == occurrences
            ? text.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirstN(text, oldText, newText, toReplace);

        string? backup = BackupExistingFile(full);
        File.WriteAllText(full, updated, new UTF8Encoding(bom));

        if (ext is ".py" or ".pyw")
        {
            ToolResult syntax = await CheckPythonSyntaxAsync(full, Path.GetDirectoryName(full) ?? workspace, ct);
            if (!syntax.Success)
            {
                return new ToolResult(false,
                    $"已替换 {full} 中的 {toReplace} 处，但 Python 语法检查未通过：\n{syntax.Output}\n" +
                    "请修正后重新检查。");
            }
        }

        return new ToolResult(true,
            $"已替换：{full}，共 {toReplace} 处（原文件共 {occurrences} 处）" +
            (backup != null ? $"\n原文件备份：{backup}" : ""));
    }

    private static async Task<ToolResult> FetchUrlAsync(string url, int maxChars, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
            return new ToolResult(false, "URL 无效，只支持 http/https。");

        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, ct);

            string mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (mediaType.StartsWith("image/")
                || mediaType.Contains("pdf")
                || mediaType.Contains("zip")
                || mediaType.Contains("octet-stream"))
            {
                return new ToolResult(false,
                    $"URL 返回二进制内容（{mediaType}），请用 download_file 下载后再处理。");
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            if (mediaType.Contains("html") || content.TrimStart().StartsWith("<"))
                content = HtmlToText(content);

            int limit = Math.Clamp(maxChars, 1000, 20000);
            bool truncated = content.Length > limit;
            if (truncated) content = content[..limit];

            string output = $"URL: {uri}\n状态码: {(int)response.StatusCode}\n\n{content.Trim()}" +
                            (truncated ? "\n…（已截断）" : "");

            return new ToolResult(response.IsSuccessStatusCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult(false, "请求超过 30 秒，已中止。");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "读取 URL 失败：" + ex.Message);
        }
    }

    private static string HtmlToText(string html)
    {
        string text = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<style\b[^>]*>.*?</style>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<noscript\b[^>]*>.*?</noscript>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @"\r\n?|\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static async Task<ToolResult> DownloadFileAsync(
        string workspace, string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
            return new ToolResult(false, "URL 无效，只支持 http/https。");

        string fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        string full;
        if (string.IsNullOrWhiteSpace(path))
        {
            full = Path.Combine(workspace, fileName);
        }
        else
        {
            full = ResolvePath(workspace, path);
            if (Directory.Exists(full))
                full = Path.Combine(full, fileName);
        }

        string? dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);

        string? backup = File.Exists(full) ? BackupExistingFile(full) : null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using HttpResponseMessage response = await DownloadHttp.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            if (!response.IsSuccessStatusCode)
                return new ToolResult(false, $"下载失败：HTTP {(int)response.StatusCode}");

            long total = response.Content.Headers.ContentLength ?? -1;
            using Stream source = await response.Content.ReadAsStreamAsync(linked.Token);

            string part = full + ".part";

            try
            {
                await using var target = new FileStream(
                    part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long received = 0;
                int read;

                progress?.Report(0);

                while ((read = await source.ReadAsync(buffer, linked.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    received += read;

                    if (total > 0)
                        progress?.Report(Math.Clamp(received * 100.0 / total, 0, 100));
                    else
                        progress?.Report(-1);
                }
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }

            File.Move(part, full, overwrite: true);
            progress?.Report(100);

            return new ToolResult(true,
                $"已下载：{full}（{FormatSize(new FileInfo(full).Length)}）" +
                (backup != null ? $"\n原文件备份：{backup}" : ""));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult(false, "下载超过 10 分钟，已中止。");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "下载失败：" + ex.Message);
        }
    }

    private static ToolResult CaptureScreen(string workspace, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
            return new ToolResult(false, "无法获取屏幕尺寸。");

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string full;

        if (string.IsNullOrWhiteSpace(path))
        {
            full = Path.Combine(workspace, "screenshots", $"screen_{stamp}.png");
        }
        else
        {
            full = ResolvePath(workspace, path);
            if (Directory.Exists(full))
                full = Path.Combine(full, $"screen_{stamp}.png");
        }

        string? dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(full, ImageFormat.Png);

        return new ToolResult(true,
            $"已截屏：{full}（{width}x{height}，{FormatSize(new FileInfo(full).Length)}）");
    }

    private static ToolResult GetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"系统：{RuntimeInformation.OSDescription}");
        sb.AppendLine($"机器：{Environment.MachineName}  用户：{Environment.UserName}");
        sb.AppendLine($"CPU：{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
        sb.AppendLine($"逻辑处理器：{Environment.ProcessorCount}");

        (double available, double total) = WenAgent.SystemMemory.Get();
        sb.AppendLine($"内存：可用 {available:F1} GB / 总计 {total:F1} GB");

        sb.AppendLine("磁盘：");
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives()
                         .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                sb.AppendLine($"  {drive.Name}  可用 {FormatSize(drive.AvailableFreeSpace)} / " +
                              $"总计 {FormatSize(drive.TotalSize)}");
            }
        }
        catch
        {
        }

        using var process = Process.GetCurrentProcess();
        sb.AppendLine($"WenAgent 内存：{process.WorkingSet64 / 1024.0 / 1024.0:F0} MB");

        return new ToolResult(true, sb.ToString().TrimEnd());
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirstN(string text, string oldText, string newText, int count)
    {
        var sb = new StringBuilder(text.Length + (newText.Length - oldText.Length) * count);
        int position = 0;
        int replaced = 0;

        while (replaced < count)
        {
            int hit = text.IndexOf(oldText, position, StringComparison.Ordinal);
            if (hit < 0) break;

            sb.Append(text, position, hit - position).Append(newText);
            position = hit + oldText.Length;
            replaced++;
        }

        sb.Append(text, position, text.Length - position);
        return sb.ToString();
    }

    private static string? BackupExistingFile(string full)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WenAgent", "backups", stamp);

            Directory.CreateDirectory(backupDir);
            string destination = Path.Combine(backupDir, Path.GetFileName(full));
            File.Copy(full, destination, overwrite: false);
            return destination;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WenAgent/1.0");
        return client;
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

    private static async Task<ToolResult> WriteFileAsync(
        string workspace, string path, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return new ToolResult(false, "缺少参数 path。");

        string full = ResolvePath(workspace, path);
        string ext = Path.GetExtension(full).ToLowerInvariant();

        if (BinaryExtensions.Contains(ext))
        {
            return new ToolResult(false,
                $"{ext} 是二进制/Office 文件，write_file 会覆盖并损坏它，已拒绝写入。\n" +
                "请改为：先用 write_file 写一个 .py 脚本，再用 run_python 通过 openpyxl/python-docx 等库修改；" +
                "run_python 会自动备份同目录下的 Office 文件。");
        }

        string? dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);

        File.WriteAllText(full, content, new UTF8Encoding(false));

        if (ext is ".py" or ".pyw")
        {
            ToolResult syntax = await CheckPythonSyntaxAsync(full, dir ?? workspace, ct);
            if (!syntax.Success)
            {
                return new ToolResult(false,
                    $"已写入：{full}\nPython 语法检查未通过：\n{syntax.Output}\n" +
                    "多行字符串请用三引号；修正引号/缩进后重新 write_file，再 run_python。");
            }
        }

        return new ToolResult(true,
            $"已写入：{full}（{content.Length} 字符，{FormatSize(new FileInfo(full).Length)}）");
    }

    private static async Task<ToolResult> CheckPythonSyntaxAsync(
        string scriptPath, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())");
        psi.ArgumentList.Add(scriptPath);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        return await RunProcessAsync(psi, ct);
    }

    private static async Task<ToolResult> RunPythonAsync(
        string workspace, string script, bool allowCommands, CancellationToken ct)
    {
        if (!allowCommands)
            return new ToolResult(false, "用户已在界面上关闭“允许执行命令”，请不要重试，改用 final 说明。");

        if (string.IsNullOrWhiteSpace(script))
            return new ToolResult(false, "缺少参数 script。");

        ct.ThrowIfCancellationRequested();

        string full = ResolvePath(workspace, script);
        if (!File.Exists(full))
            return new ToolResult(false,
                $"脚本不存在：{full}\n请先用 write_file 创建脚本，再调用 run_python。");

        string scriptDir = Path.GetDirectoryName(full) ?? workspace;

        string backupNote = "";
        try
        {
            backupNote = BackupOfficeFiles(scriptDir);
        }
        catch (Exception ex)
        {
            backupNote = "警告：Office 文件自动备份失败：" + ex.Message;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(full);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        ToolResult result = await RunProcessAsync(psi, ct);

        if (backupNote.Length > 0)
            result = result with { Output = backupNote + "\n" + result.Output };

        return result;
    }

    private static string BackupOfficeFiles(string dir)
    {
        string[] patterns = ["*.xlsx", "*.xls", "*.xlsm", "*.docx", "*.doc"];
        var files = new List<string>();

        foreach (string pattern in patterns)
            files.AddRange(Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly));

        if (files.Count == 0) return "";

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WenAgent", "backups", stamp);

        Directory.CreateDirectory(backupDir);

        foreach (string file in files)
            File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: false);

        return $"已自动备份 {files.Count} 个 Office 文件到：{backupDir}";
    }

    public static string? GetMissingPythonModule(string output)
    {
        Match match = Regex.Match(output, @"No module named '([^']+)'");
        if (!match.Success) return null;

        string module = match.Groups[1].Value;
        int dot = module.IndexOf('.');
        return dot > 0 ? module[..dot] : module;
    }

    public static string? MapPythonPackage(string module) => module.ToLowerInvariant() switch
    {
        "openpyxl" => "openpyxl",
        "docx" => "python-docx",
        "pptx" => "python-pptx",
        "pypdf" => "pypdf",
        "pypdf2" => "pypdf",
        "pdfplumber" => "pdfplumber",
        "fitz" => "pymupdf",
        "pil" => "pillow",
        "cv2" => "opencv-python",
        "numpy" => "numpy",
        "pandas" => "pandas",
        "matplotlib" => "matplotlib",
        "bs4" => "beautifulsoup4",
        "yaml" => "pyyaml",
        "lxml" => "lxml",
        "requests" => "requests",
        "xlrd" => "xlrd",
        "xlsxwriter" => "xlsxwriter",
        "win32com" => "pywin32",
        "onnxruntime" => "onnxruntime",
        "scipy" => "scipy",
        _ => null
    };

    public static async Task<ToolResult> InstallPythonPackageAsync(
        string package, Action<string> onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(package))
            return new ToolResult(false, "Python 包名为空。");

        onProgress($"正在下载/安装 Python 包：{package}");

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--user");
        psi.ArgumentList.Add("--disable-pip-version-check");
        psi.ArgumentList.Add("--no-input");
        psi.ArgumentList.Add("--progress-bar");
        psi.ArgumentList.Add("off");
        psi.ArgumentList.Add(package);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        ToolResult result = await RunProcessAsync(psi, ct, TimeSpan.FromMinutes(5));
        onProgress(result.Success
            ? $"Python 包 {package} 安装完成"
            : $"Python 包 {package} 安装失败");

        return result;
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

        return await RunProcessAsync(psi, ct);
    }

    private static async Task<ToolResult> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new ToolResult(false, $"无法启动进程：{psi.FileName}");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

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
                $"命令超过 {limit.TotalSeconds:F0} 秒，已终止。\n{Truncate(partial, MaxOutputChars)}");
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

    private static bool GetBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
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
