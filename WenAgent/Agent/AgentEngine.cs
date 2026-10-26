using System.IO;
using System.Text.Json;

namespace WenAgent.Agent;

public enum AgentEventKind
{
    GenerationStarted,
    Token,
    StepStarted,
    ToolResult,
    Final,
    Info,
    Progress
}

public sealed record AgentEvent(
    AgentEventKind Kind,
    string Text = "",
    string Detail = "",
    bool Success = true,
    double Percent = -1);

public sealed class AgentEngine : IDisposable
{
    public const int MaxSteps = 12;

    private readonly LlamaRunner _runner;
    private readonly List<ChatTurn> _turns = [];
    private string _systemPrompt;
    private bool _needsReset;

    public AgentEngine(string modelPath, string? workspace = null)
    {
        WorkspacePath = Path.GetFullPath(workspace ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WenAgentWorkspace"));
        Directory.CreateDirectory(WorkspacePath);

        _systemPrompt = AgentPrompts.BuildSystemPrompt(WorkspacePath, MaxSteps);
        _runner = new LlamaRunner(modelPath);

        foreach (ChatTurn turn in WenAgent.ChatStore.LoadTurns().TakeLast(4))
            _turns.Add(turn);
    }

    public string WorkspacePath { get; private set; }
    public string ModelPath => _runner.ModelPath;
    public bool AllowCommands { get; set; } = true;

    public void SetWorkspace(string path, bool resetConversation = true)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);

        WorkspacePath = full;
        _systemPrompt = AgentPrompts.BuildSystemPrompt(full, MaxSteps);
        _needsReset = true;

        if (resetConversation)
        {
            _turns.Clear();
            WenAgent.ChatStore.ClearTurns();
            if (_runner.IsLoaded) PrepareSession();
        }
    }

    public async Task LoadAsync(IProgress<string>? status, CancellationToken ct)
    {
        await _runner.LoadAsync(_systemPrompt, status, ct);

        if (_turns.Count > 0)
        {
            _needsReset = true;
            PrepareSession();
        }
    }

    public async Task RunAsync(string task, IProgress<AgentEvent> sink, CancellationToken ct)
    {
        if (_runner.HistoryCharCount > 12000) _needsReset = true;
        PrepareSession();

        string input = task;
        int repairs = 0;
        int emptyRetries = 0;
        int falseClaimRetries = 0;
        bool mutationToolSucceeded = false;
        bool taskNeedsMutation = TaskRequiresFileMutation(task);

        try
        {
            for (int step = 1; step <= MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                sink.Report(new AgentEvent(AgentEventKind.GenerationStarted, step.ToString(), $"第 {step} 步"));

                string raw = await _runner.RunStepAsync(input,
                    token => sink.Report(new AgentEvent(AgentEventKind.Token, token)), ct);

                AgentStep parsed = AgentStepParser.Parse(raw);

                switch (parsed.Kind)
                {
                    case StepKind.Final:
                    {
                        string answer = parsed.Answer.Trim();
                        if (answer.Length == 0)
                        {
                            if (TryRecoverEmpty(task, input, ref emptyRetries, out string retryInput))
                            {
                                input = retryInput;
                                continue;
                            }

                            answer = "模型本次没有生成内容。这通常是上下文过长或模型偶发空回复；" +
                                     "请点“新对话”清空上下文后再试，或把任务拆小。";
                            _needsReset = true;
                        }

                        if (!mutationToolSucceeded && (taskNeedsMutation || LooksLikeCompletionClaim(answer)))
                        {
                            if (falseClaimRetries < 1)
                            {
                                falseClaimRetries++;
                                input = "你还没有成功调用任何会修改/生成文件的工具（write_file 写数据文件、run_python、run_command），" +
                                        "不能声称已完成。请实际执行；write_file 必须同时提供 path 和 content。" +
                                        "只有工具成功返回后才能 final。";
                                continue;
                            }

                            answer = "本次没有实际修改/生成文件：模型声称已完成，但没有成功调用写入/执行工具。" +
                                     "请重试，或点“新对话”后重新执行。";
                            _needsReset = true;
                        }

                        FinishAnswer(task, answer, step, sink);
                        return;
                    }
                    case StepKind.Tool:
                    {
                        sink.Report(new AgentEvent(AgentEventKind.StepStarted, parsed.Tool, step.ToString()));

                        ToolResult result;
                        if (parsed.Tool.Equals("set_workspace", StringComparison.OrdinalIgnoreCase))
                            result = ChangeWorkspace(parsed.ArgsJson);
                        else if (parsed.Tool.Equals("inspect_image", StringComparison.OrdinalIgnoreCase))
                            result = await InspectImageAsync(parsed.ArgsJson, sink, ct);
                        else if (parsed.Tool.Equals("download_file", StringComparison.OrdinalIgnoreCase))
                            result = await DownloadFileAsync(parsed.ArgsJson, sink, ct);
                        else
                            result = await AgentTools.ExecuteAsync(
                                parsed.Tool, parsed.ArgsJson, WorkspacePath, AllowCommands, ct);

                        if (!result.Success
                            && parsed.Tool.Equals("run_python", StringComparison.OrdinalIgnoreCase))
                        {
                            string? module = AgentTools.GetMissingPythonModule(result.Output);
                            string? package = module == null ? null : AgentTools.MapPythonPackage(module);

                            if (package != null)
                            {
                                try
                                {
                                    sink.Report(new AgentEvent(AgentEventKind.Progress,
                                        $"缺少 Python 模块 {module}，正在自动安装 {package}…"));

                                    ToolResult install = await AgentTools.InstallPythonPackageAsync(
                                        package,
                                        message => sink.Report(new AgentEvent(AgentEventKind.Progress, message)),
                                        ct);

                                    if (install.Success)
                                    {
                                        sink.Report(new AgentEvent(AgentEventKind.Progress,
                                            "安装完成，正在重试脚本…"));

                                        result = await AgentTools.ExecuteAsync(
                                            parsed.Tool, parsed.ArgsJson, WorkspacePath, AllowCommands, ct);
                                        result = result with
                                        {
                                            Output = $"已自动安装 Python 包 {package}。\n" + result.Output
                                        };
                                    }
                                    else
                                    {
                                        result = install with
                                        {
                                            Output = $"自动安装 {package} 失败：\n{install.Output}"
                                        };
                                    }
                                }
                                finally
                                {
                                    sink.Report(new AgentEvent(AgentEventKind.Progress, ""));
                                }
                            }
                            else if (module != null)
                            {
                                result = result with
                                {
                                    Output = result.Output +
                                             $"\n\n缺少 Python 模块 {module}，没有自动安装映射。"
                                };
                            }
                        }

                        string output = Truncate(result.Output, 6000);

                        if (ToolCountsAsMutation(parsed.Tool, parsed.ArgsJson, result.Success))
                            mutationToolSucceeded = true;
                        sink.Report(new AgentEvent(AgentEventKind.ToolResult, output, parsed.Tool, result.Success));

                        input = $"""
                            工具 {parsed.Tool} 的执行结果：
                            {output}

                            请根据结果继续。下一步只输出一个 JSON 对象。
                            """;
                        continue;
                    }
                    case StepKind.Invalid:
                    {
                        if (repairs < 1)
                        {
                            repairs++;
                            input = "你上一条输出不是合法的协议 JSON。请严格按照协议，只输出一个 JSON 对象，不要输出其他文字。";
                            continue;
                        }

                        string fallback = parsed.Raw.Trim();
                        if (fallback.Length == 0)
                        {
                            if (TryRecoverEmpty(task, input, ref emptyRetries, out string retryInput))
                            {
                                input = retryInput;
                                continue;
                            }

                            fallback = "模型本次没有生成可解析的内容。请点“新对话”清空上下文后再试。";
                            _needsReset = true;
                        }

                        if (!mutationToolSucceeded && (taskNeedsMutation || LooksLikeCompletionClaim(fallback)))
                        {
                            if (falseClaimRetries < 1)
                            {
                                falseClaimRetries++;
                                input = "你还没有成功调用任何会修改/生成文件的工具（write_file 写数据文件、run_python、run_command），" +
                                        "不能声称已完成。请实际执行；write_file 必须同时提供 path 和 content。" +
                                        "只有工具成功返回后才能 final。";
                                continue;
                            }

                            fallback = "本次没有实际修改/生成文件：模型声称已完成，但没有成功调用写入/执行工具。" +
                                       "请重试，或点“新对话”后重新执行。";
                            _needsReset = true;
                        }

                        FinishAnswer(task, fallback, step, sink);
                        return;
                    }
                    default:
                    {
                        string answer = parsed.Answer.Trim();
                        if (answer.Length == 0)
                        {
                            if (TryRecoverEmpty(task, input, ref emptyRetries, out string retryInput))
                            {
                                input = retryInput;
                                continue;
                            }

                            answer = "模型本次没有生成内容。这通常是上下文过长或模型偶发空回复；" +
                                     "请点“新对话”清空上下文后再试，或把任务拆小。";
                            _needsReset = true;
                        }

                        if (!mutationToolSucceeded && (taskNeedsMutation || LooksLikeCompletionClaim(answer)))
                        {
                            if (falseClaimRetries < 1)
                            {
                                falseClaimRetries++;
                                input = "你还没有成功调用任何会修改/生成文件的工具（write_file 写数据文件、run_python、run_command），" +
                                        "不能声称已完成。请实际执行；write_file 必须同时提供 path 和 content。" +
                                        "只有工具成功返回后才能 final。";
                                continue;
                            }

                            answer = "本次没有实际修改/生成文件：模型声称已完成，但没有成功调用写入/执行工具。" +
                                     "请重试，或点“新对话”后重新执行。";
                            _needsReset = true;
                        }

                        FinishAnswer(task, answer, step, sink);
                        return;
                    }
                }
            }

            _needsReset = true;
            sink.Report(new AgentEvent(AgentEventKind.Final,
                $"已达到最大步骤数（{MaxSteps}），任务暂停。你可以继续追问，或换一种说法再试。"));
        }
        catch (OperationCanceledException)
        {
            _needsReset = true;
            _runner.Invalidate();
            throw;
        }
        catch
        {
            _needsReset = true;
            _runner.Invalidate();
            throw;
        }
    }

    private ToolResult ChangeWorkspace(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string path = doc.RootElement.TryGetProperty("path", out JsonElement value)
                          && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(path))
                return new ToolResult(false, "缺少参数 path。");

            string full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path.Trim().Trim('"'))
                : Path.GetFullPath(Path.Combine(WorkspacePath, path.Trim().Trim('"')));

            if (!Directory.Exists(full))
                return new ToolResult(false, $"目录不存在：{full}");

            SetWorkspace(full, resetConversation: false);
            return new ToolResult(true,
                $"工作目录已切换为：{full}\n后续相对路径都基于该目录；请继续完成当前任务。");
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "切换工作目录失败：" + ex.Message);
        }
    }

    private async Task<ToolResult> InspectImageAsync(
        string argsJson, IProgress<AgentEvent> sink, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;

            string path = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? ""
                : "";
            string question = root.TryGetProperty("question", out JsonElement q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(path))
                return new ToolResult(false, "缺少参数 path。");

            string full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path.Trim().Trim('"'))
                : Path.GetFullPath(Path.Combine(WorkspacePath, path.Trim().Trim('"')));

            if (!File.Exists(full))
                return new ToolResult(false,
                    $"图片不存在：{full}\n请先用 find_files 找到图片，或传绝对路径。");

            sink.Report(new AgentEvent(AgentEventKind.Info,
                "开始视觉检测：加载 Qwen2.5-VL-3B（约 2.8GB），首次加载约 10-30 秒，CPU 推理可能需要 1-3 分钟。"));

            VisionResult result = await VisionRunner.InspectAsync(
                full, question,
                new Progress<string>(message => sink.Report(new AgentEvent(AgentEventKind.Info, message))),
                ct);

            string output = "视觉检测结果：\n" + result.Text;
            if (!string.IsNullOrEmpty(result.AnnotatedImage))
                output += "\n\n标注图：" + result.AnnotatedImage;

            return new ToolResult(true, output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LlamaLog.SaveToFile();

            string message = ex.Message;
            if (message.Contains("Failed to load model", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase))
            {
                message += "\n\n可能是可用内存不足（视觉模型额外需要约 3GB）。请关闭 VS/浏览器等程序后重试。";
            }

            return new ToolResult(false, "视觉检测失败：" + message);
        }
    }

    private static readonly string[] CompletionClaimMarkers =
    [
        "已添加", "已修改", "已写入", "已保存", "已更新", "已创建", "已将",
        "已经添加", "已经修改", "已经写入", "已经保存", "已经更新",
        "成功添加", "成功修改", "成功写入", "成功保存",
        "成功创建", "生成成功", "已生成", "生成了", "创建完成", "添加完成", "修改完成", "写入完成", "保存完成"
    ];

    private static readonly string[] MutationActions =
    [
        "添加", "修改", "写入", "保存", "创建", "生成", "更新", "删除",
        "整理", "导出", "转换", "备份", "写一个", "写一份", "运行"
    ];

    private static readonly string[] FileHints =
    [
        "文件", "脚本", "目录", "表格", "Excel", "Word", "PDF", "图片", "路径",
        ".py", ".csv", ".xlsx", ".docx", ".pdf", ".png", ".jpg", ".jpeg",
        ".txt", ".md", ".json", ".flow", ".pptx"
    ];

    private static bool TaskRequiresFileMutation(string task)
    {
        bool hasAction = MutationActions.Any(action => task.Contains(action, StringComparison.OrdinalIgnoreCase));
        bool hasFile = FileHints.Any(hint => task.Contains(hint, StringComparison.OrdinalIgnoreCase));
        return hasAction && hasFile;
    }

    private static bool LooksLikeCompletionClaim(string text)
        => CompletionClaimMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    private static bool ToolCountsAsMutation(string tool, string argsJson, bool success)
    {
        if (!success) return false;

        if (tool.Equals("run_python", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("run_command", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("replace_in_file", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("download_file", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("capture_screen", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!tool.Equals("write_file", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            string path = doc.RootElement.TryGetProperty("path", out JsonElement value)
                          && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is not ".py" and not ".pyw" and not ".ps1" and not ".sh" and not ".bat" and not ".cmd";
        }
        catch
        {
            return false;
        }
    }

    private bool TryRecoverEmpty(string task, string previousInput, ref int emptyRetries, out string retryInput)
    {
        retryInput = "";

        if (emptyRetries >= 1) return false;
        emptyRetries++;

        // 空回复往往是上下文被撑满；用已完成轮次重建会话，再把原任务和最近一次输入带回去
        _needsReset = true;
        PrepareSession();

        retryInput = $"原始任务：{task}\n\n上一次的输入/工具结果：\n{previousInput}\n\n" +
                     "请继续完成原始任务。如果已经完成，只输出 {\"action\":\"final\",\"answer\":\"...\"}。";
        return true;
    }

    private void FinishAnswer(string task, string answer, int step, IProgress<AgentEvent> sink)
    {
        sink.Report(new AgentEvent(AgentEventKind.Final, answer));
        _turns.Add(new ChatTurn(task, answer));
        WenAgent.ChatStore.SaveTurns(_turns);

        // 多步任务会留下大量工具输出，下一轮开始前压缩上下文，避免 16k 被撑满
        if (step >= 5) _needsReset = true;
    }

    private async Task<ToolResult> DownloadFileAsync(
        string argsJson, IProgress<AgentEvent> sink, CancellationToken ct)
    {
        string name = "文件";

        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;

            string url = root.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? ""
                : "";
            string path = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(url))
                return new ToolResult(false, "缺少参数 url。");

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                string fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                if (!string.IsNullOrWhiteSpace(fileName)) name = fileName;
            }

            var progress = new Progress<double>(percent =>
            {
                string text = percent < 0
                    ? $"正在下载 {name}…（大小未知）"
                    : $"正在下载 {name}… {percent:F0}%";

                sink.Report(new AgentEvent(
                    AgentEventKind.Progress, text, "", true, percent < 0 ? -1 : percent));
            });

            return await AgentTools.DownloadFileAsync(WorkspacePath, url, path, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(false, "下载失败：" + ex.Message);
        }
        finally
        {
            sink.Report(new AgentEvent(AgentEventKind.Progress, ""));
        }
    }

    public void NewConversation()
    {
        _turns.Clear();
        WenAgent.ChatStore.ClearTurns();
        _needsReset = true;
        PrepareSession();
    }

    private void PrepareSession()
    {
        if (!_needsReset && !_runner.NeedsReset && _runner.IsLoaded) return;

        List<ChatTurn> replay = _turns
            .TakeLast(4)
            .Select(t => new ChatTurn(
                Truncate(t.User, 2000),
                Truncate(t.Assistant, 800)))
            .ToList();

        _runner.Rebuild(replay, _systemPrompt);
        _needsReset = false;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _runner.Dispose();
}
