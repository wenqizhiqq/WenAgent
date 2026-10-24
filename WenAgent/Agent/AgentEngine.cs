using System.IO;

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

    public void SetWorkspace(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);

        WorkspacePath = full;
        _systemPrompt = AgentPrompts.BuildSystemPrompt(full, MaxSteps);
        _turns.Clear();
        _needsReset = true;

        if (_runner.IsLoaded) PrepareSession();
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

                        ToolResult result = await AgentTools.ExecuteAsync(
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
