using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Sampling;

namespace WenAgent.Agent;

public sealed record ChatTurn(string User, string Assistant);

/// <summary>
/// Qwen 的 ChatML 模板。ChatSession 增量续写时只渲染当前消息，
/// 这里补上上一轮缺少的 &lt;|im_end|&gt; 结束符。
/// </summary>
public sealed class ChatMlHistoryTransform : IHistoryTransform
{
    public string HistoryToText(ChatHistory history)
    {
        var sb = new StringBuilder();

        if (history.Messages.Count == 1)
            sb.Append("<|im_end|>\n");

        foreach (var message in history.Messages)
        {
            sb.Append("<|im_start|>")
              .Append(RoleName(message.AuthorRole))
              .Append('\n')
              .Append(message.Content)
              .Append("<|im_end|>\n");
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    public ChatHistory TextToHistory(AuthorRole role, string text)
        => new([new ChatHistory.Message(role, text)]);

    public IHistoryTransform Clone() => new ChatMlHistoryTransform();

    private static string RoleName(AuthorRole role) => role switch
    {
        AuthorRole.System => "system",
        AuthorRole.Assistant => "assistant",
        _ => "user"
    };
}

public sealed class LlamaRunner : IDisposable
{
    private const uint ContextSize = 16384;

    private readonly string _modelPath;
    private ModelParams? _parameters;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ChatSession? _session;
    private bool _dirty = true;
    private int _lastLoadPercent = -1;

    public LlamaRunner(string modelPath) => _modelPath = modelPath;

    public string ModelPath => _modelPath;
    public bool IsLoaded => _session != null;
    public bool NeedsReset => _dirty;

    public int HistoryCharCount
    {
        get
        {
            try
            {
                return _session?.History.Messages.Sum(message => message.Content.Length) ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public async Task LoadAsync(string systemPrompt, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("正在加载模型…");
        _lastLoadPercent = -1;
        LlamaLog.Install();
        LlamaLog.Clear();

        _parameters = new ModelParams(_modelPath)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            BatchSize = 512,
            UBatchSize = 256,
            Threads = Math.Min(14, Environment.ProcessorCount)
        };

        var progress = new Progress<float>(p =>
        {
            int percent = (int)(p * 100);
            if (percent == _lastLoadPercent) return;
            _lastLoadPercent = percent;
            status?.Report($"正在加载模型… {percent}%");
        });
        _weights = await LLamaWeights.LoadFromFileAsync(_parameters, ct, progress);
        Rebuild([], systemPrompt);
        status?.Report("模型已就绪");
    }

    public void Rebuild(IReadOnlyList<ChatTurn> turns, string systemPrompt)
    {
        if (_weights == null || _parameters == null)
            throw new InvalidOperationException("模型尚未加载。");

        _context?.Dispose();
        _context = _weights.CreateContext(_parameters);

        _session = new ChatSession(new InteractiveExecutor(_context))
            .WithHistoryTransform(new ChatMlHistoryTransform());
        _session.AddSystemMessage(systemPrompt);

        foreach (var turn in turns)
        {
            _session.AddUserMessage(turn.User);
            _session.AddAssistantMessage(turn.Assistant);
        }

        _dirty = false;
    }

    public async Task<string> RunStepAsync(string userMessage, Action<string> onDelta, CancellationToken ct)
    {
        ChatSession session = _session ?? throw new InvalidOperationException("模型尚未加载完成。");
        ct.ThrowIfCancellationRequested();

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 1024,
            AntiPrompts = ["<|im_end|>", "<|im_start|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.15f,
                TopP = 0.9f,
                RepeatPenalty = 1.05f
            }
        };

        var sb = new StringBuilder();
        var message = new ChatHistory.Message(AuthorRole.User, userMessage);

        await foreach (string token in session.ChatAsync(message, inferenceParams, ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(token);
            onDelta(token);
        }

        // LLamaSharp 检测到取消时只是结束生成流，不会抛异常，这里显式抛出交给上层处理
        ct.ThrowIfCancellationRequested();
        return sb.ToString();
    }

    /// <summary>中断后会话内容不完整，下次运行前必须重建。</summary>
    public void Invalidate() => _dirty = true;

    public void Dispose()
    {
        _session = null;
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
    }
}
