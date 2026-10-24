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
    Info
}

public sealed record AgentEvent(AgentEventKind Kind, string Text = "", string Detail = "", bool Success = true);

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
            if (_runner.IsLoaded) PrepareSession();
        }
    }

    public Task LoadAsync(IProgress<string>? status, CancellationToken ct)
        => _runner.LoadAsync(_systemPrompt, status, ct);

    public async Task RunAsync(string task, IProgress<AgentEvent> sink, CancellationToken ct)
    {
        PrepareSession();

        string input = task;
        int repairs = 0;

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
                        if (answer.Length == 0) answer = "（模型没有返回内容）";
                        sink.Report(new AgentEvent(AgentEventKind.Final, answer));
                        _turns.Add(new ChatTurn(task, answer));
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
                        else
                            result = await AgentTools.ExecuteAsync(
                                parsed.Tool, parsed.ArgsJson, WorkspacePath, AllowCommands, ct);

                        string output = Truncate(result.Output, 6000);
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
                        if (fallback.Length == 0) fallback = "（模型没有返回可解析的内容）";
                        sink.Report(new AgentEvent(AgentEventKind.Final, fallback));
                        _turns.Add(new ChatTurn(task, fallback));
                        return;
                    }
                    default:
                    {
                        string answer = parsed.Answer.Trim();
                        if (answer.Length == 0) answer = "（模型没有返回内容）";
                        sink.Report(new AgentEvent(AgentEventKind.Final, answer));
                        _turns.Add(new ChatTurn(task, answer));
                        return;
                    }
                }
            }

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

    public void NewConversation()
    {
        _turns.Clear();
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
