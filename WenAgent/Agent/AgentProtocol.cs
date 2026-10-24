using System.Text;
using System.Text.Json;

namespace WenAgent.Agent;

public enum StepKind
{
    Tool,
    Final,
    Invalid,
    RawAnswer
}

public sealed record AgentStep(StepKind Kind, string Tool, string ArgsJson, string Answer, string Raw);

public static class AgentStepParser
{
    public static AgentStep Parse(string raw)
    {
        string text = raw.Trim();
        string? json = ExtractFirstJsonObject(text, out bool sawBrace);

        if (json == null)
        {
            if (sawBrace)
            {
                AgentStep recovered = TryRecoverFinal(text, raw);
                if (recovered.Kind != StepKind.Invalid) return recovered;
                return new AgentStep(StepKind.Invalid, "", "{}", "", raw);
            }

            return new AgentStep(StepKind.RawAnswer, "", "{}", CleanText(text), raw);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(EscapeRawControlChars(json));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new AgentStep(StepKind.Invalid, "", "{}", "", raw);

            string action = GetString(root, "action").Trim().ToLowerInvariant();

            if (action == "tool")
            {
                string tool = GetString(root, "tool").Trim();
                if (tool.Length == 0)
                    return new AgentStep(StepKind.Invalid, "", "{}", "", raw);

                string args = root.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Object
                    ? a.GetRawText()
                    : "{}";

                return new AgentStep(StepKind.Tool, tool, args, "", raw);
            }

            string answer = GetString(root, "answer").Trim();
            if (action == "final" && answer.Length > 0)
                return new AgentStep(StepKind.Final, "", "{}", answer, raw);

            if (answer.Length > 0)
                return new AgentStep(StepKind.Final, "", "{}", answer, raw);

            return new AgentStep(StepKind.Invalid, "", "{}", "", raw);
        }
        catch (JsonException)
        {
            AgentStep recovered = TryRecoverFinal(json, raw);
            return recovered.Kind != StepKind.Invalid
                ? recovered
                : new AgentStep(StepKind.Invalid, "", "{}", "", raw);
        }
    }

    /// <summary>
    /// 模型偶尔会在 JSON 字符串里放真实换行（代码块），这不是合法 JSON。
    /// 转义字符串内的控制字符后再解析。
    /// </summary>
    private static string EscapeRawControlChars(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool escaped = false;

        foreach (char c in json)
        {
            if (inString)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                }
                else if (c == '\\')
                {
                    sb.Append(c);
                    escaped = true;
                }
                else if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else if (c < 0x20)
                {
                    sb.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"') inString = true;
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// JSON 结构被代码里的引号/换行破坏时，尽量从 final 文本里把 answer 捞出来。
    /// </summary>
    private static AgentStep TryRecoverFinal(string candidate, string raw)
    {
        int actionKey = candidate.IndexOf("\"action\"", StringComparison.Ordinal);
        if (actionKey < 0) return Invalid(raw);

        int actionColon = candidate.IndexOf(':', actionKey + 8);
        if (actionColon < 0) return Invalid(raw);

        int actionQuote = candidate.IndexOf('"', actionColon + 1);
        if (actionQuote < 0) return Invalid(raw);

        int actionEnd = candidate.IndexOf('"', actionQuote + 1);
        if (actionEnd < 0) return Invalid(raw);

        string action = candidate[(actionQuote + 1)..actionEnd].Trim();
        if (!action.Equals("final", StringComparison.OrdinalIgnoreCase)) return Invalid(raw);

        int answerKey = candidate.IndexOf("\"answer\"", StringComparison.Ordinal);
        if (answerKey < 0) return Invalid(raw);

        int answerColon = candidate.IndexOf(':', answerKey + 8);
        if (answerColon < 0) return Invalid(raw);

        int answerQuote = candidate.IndexOf('"', answerColon + 1);
        if (answerQuote < 0) return Invalid(raw);

        int answerEnd = candidate.LastIndexOf('"');
        if (answerEnd <= answerQuote) return Invalid(raw);

        string content = UnescapeJsonText(candidate[(answerQuote + 1)..answerEnd]).Trim();
        return content.Length == 0
            ? Invalid(raw)
            : new AgentStep(StepKind.Final, "", "{}", content, raw);
    }

    private static AgentStep Invalid(string raw)
        => new(StepKind.Invalid, "", "{}", "", raw);

    private static string UnescapeJsonText(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = text[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u':
                    if (i + 4 < text.Length
                        && ushort.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ushort code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else
                    {
                        sb.Append("\\u");
                    }
                    break;
                default:
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string CleanText(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;

        int firstLine = t.IndexOf('\n');
        if (firstLine < 0) return t.Trim('`').Trim();
        t = t[(firstLine + 1)..];
        int fence = t.LastIndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) t = t[..fence];
        return t.Trim();
    }

    private static string? ExtractFirstJsonObject(string text, out bool sawBrace)
    {
        sawBrace = false;
        int start = text.IndexOf('{');
        if (start < 0) return null;
        sawBrace = true;

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }

        return null;
    }
}

public static class AgentPrompts
{
    public static string BuildSystemPrompt(string workspace, int maxSteps) => """
        你是 WenAgent，一个运行在 Windows 本机的中文 AI 助手，可以直接读写本机文件。

        当前工作目录：{WORKSPACE}
        相对路径基于工作目录；也可以直接使用绝对路径访问本机其他文件。
        今天是 {DATE}。

        【输出协议】
        每一步只输出一个 JSON 对象，不要输出 Markdown 代码块、解释或任何其他文字。
        调用工具：{"action":"tool","tool":"工具名","args":{...}}
        结束任务：{"action":"final","answer":"给用户的最终答复"}

        【可用工具】
        - list_files  {"path":"."}                                      列出目录内容
        - read_file   {"path":"a.txt"}                                  读取文本文件（xlsx/docx/pdf 等二进制文件不支持）
        - write_file  {"path":"a.py","content":"..."}                   覆盖写入文本文件，自动创建目录
        - find_files  {"path":".","pattern":"*.xlsx","max_results":20}  递归查找文件（深度 6，最多 10 秒）
        - run_command {"command":"python a.py"}                         用 PowerShell 在工作目录执行命令，最长 60 秒

        【规则】
        1. 需要事实、文件内容或命令结果时，必须先调用工具，绝不能编造工具结果。
        2. 每次只调用一个工具；看到工具结果后再决定下一步。
        3. 最多执行 {MAX_STEPS} 步。
        4. 修改已有文件前，先用 run_command 备份（例如 Copy-Item x.xlsx x.xlsx.bak）。
        5. Excel/Word/PDF 等二进制文件不能用 read_file；先 write_file 写 Python 脚本，再用 run_command 执行。
           本机已有 Python 3.12 和 openpyxl；修改 xlsx 后要重新用脚本打开验证，确认保存成功。
        6. 任务涉及的工程或表格不在工作目录时，先用 find_files 在合理范围查找（例如 D:\wqz\code），
           仍然找不到再在 final 里请用户提供路径。
        7. 任务完成或确实无法完成时必须用 action=final；answer 要简洁，不要重复粘贴整个文件内容。
        8. JSON 字符串里的换行写成 \n，双引号写成 \"。

        示例：
        用户：看看当前目录有什么
        你：{"action":"tool","tool":"list_files","args":{"path":"."}}
        （收到工具结果后）
        你：{"action":"final","answer":"当前目录下有 3 个文件：……"}
        """
        .Replace("{WORKSPACE}", workspace)
        .Replace("{DATE}", DateTime.Now.ToString("yyyy-MM-dd"))
        .Replace("{MAX_STEPS}", maxSteps.ToString());
}
