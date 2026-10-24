using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace WenAgent.Agent;

public sealed record VisionResult(string Text, string? AnnotatedImage);

public static class VisionRunner
{
    private const uint VisionContextSize = 4096;
    private const int MaxOutputTokens = 700;

    public static async Task<VisionResult> InspectAsync(
        string imagePath, string question, IProgress<string>? status, CancellationToken ct)
    {
        (string? modelPath, string? mmprojPath) = VisionModelLocator.Resolve();
        if (modelPath == null || mmprojPath == null)
            throw new InvalidOperationException(VisionModelLocator.MissingMessage());
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"图片不存在：{imagePath}");

        LlamaLog.Install();
        LlamaLog.Clear();
        status?.Report("正在加载视觉模型（约 2.8GB，CPU 推理）…");

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = VisionContextSize,
            GpuLayerCount = 0,
            BatchSize = 512,
            UBatchSize = 256,
            Threads = Math.Min(14, Environment.ProcessorCount)
        };

        using var weights = await LLamaWeights.LoadFromFileAsync(parameters, ct);
        using var context = weights.CreateContext(parameters);

        var mtmdParams = MtmdContextParams.Default();
        mtmdParams.UseGpu = false;
        mtmdParams.NThreads = Math.Min(14, Environment.ProcessorCount);
        mtmdParams.ImageMaxTokens = 768;

        status?.Report("正在加载视觉投影（mmproj）…");
        using var clip = await MtmdWeights.LoadFromFileAsync(mmprojPath, weights, mtmdParams, ct);
        if (!clip.SupportsVision)
            throw new InvalidOperationException("当前 mmproj 不支持视觉输入。");

        string marker = mtmdParams.MediaMarker ?? NativeApi.MtmdDefaultMarker() ?? "<media>";
        var executor = new InteractiveExecutor(context, clip);
        using var embed = clip.LoadMedia(imagePath);
        executor.Embeds.Clear();
        executor.Embeds.Add(embed);

        const string systemPrompt = """
            你是工业视觉缺陷检测助手。请仔细查看用户提供的图片，检测可见缺陷。
            只输出一个 JSON 对象，不要输出 Markdown 代码块或其他文字：
            {"summary":"一句话总结","defects":[{"type":"缺陷类型","severity":"轻|中|重","bbox":[x1,y1,x2,y2],"description":"位置和外观描述"}]}
            bbox 使用 0-1000 归一化坐标，相对整张图；x1,y1 是左上角，x2,y2 是右下角。
            如果用户指定了缺陷类型，只检查这些类型；没有发现缺陷时 defects 返回空数组。
            不要臆造缺陷，不确定时降低 severity 或在 summary 中说明。
            """;

        string userPrompt = string.IsNullOrWhiteSpace(question)
            ? marker + "\n请检测这张图片中的所有可见缺陷。"
            : marker + "\n" + question.Trim();

        var template = new LLamaTemplate(weights.NativeHandle) { AddAssistant = true };
        template.Add("system", systemPrompt);
        template.Add("user", userPrompt);
        string prompt = Encoding.UTF8.GetString(template.Apply());

        var inferenceParams = new InferenceParams
        {
            MaxTokens = MaxOutputTokens,
            AntiPrompts = ["<|im_end|>", "<|im_start|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopP = 0.9f
            }
        };

        status?.Report("视觉模型正在推理（CPU 可能需要 1-3 分钟）…");
        var sb = new StringBuilder();

        await foreach (string token in executor.InferAsync(prompt, inferenceParams, ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(token);
        }

        ct.ThrowIfCancellationRequested();
        executor.Embeds.Clear();
        clip.ClearMedia();

        string text = sb.ToString().Trim();
        string? annotated = TryAnnotate(imagePath, text);
        return new VisionResult(text, annotated);
    }

    private static string? TryAnnotate(string imagePath, string modelText)
    {
        try
        {
            string? json = ExtractFirstJsonObject(modelText);
            if (json == null) return null;

            using JsonDocument doc = JsonDocument.Parse(AgentStepParser.EscapeRawControlChars(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("defects", out JsonElement defects)
                || defects.ValueKind != JsonValueKind.Array)
                return null;

            var boxes = new List<DefectBox>();

            foreach (JsonElement defect in defects.EnumerateArray())
            {
                if (defect.ValueKind != JsonValueKind.Object) continue;
                if (!defect.TryGetProperty("bbox", out JsonElement bbox) || bbox.ValueKind != JsonValueKind.Array)
                    continue;

                var numbers = bbox.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.Number)
                    .Select(v => v.GetDouble())
                    .ToArray();

                if (numbers.Length < 4) continue;

                string type = defect.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "缺陷"
                    : "缺陷";
                string severity = defect.TryGetProperty("severity", out JsonElement s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? ""
                    : "";

                boxes.Add(new DefectBox($"{type} {severity}".Trim(), numbers[0], numbers[1], numbers[2], numbers[3]));
            }

            if (boxes.Count == 0) return null;

            using var source = Image.FromFile(imagePath);
            int width = source.Width;
            int height = source.Height;

            using var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.DrawImage(source, 0, 0, width, height);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                float penWidth = Math.Max(2f, width / 400f);
                float fontSize = Math.Clamp(width / 55f, 14f, 36f);

                using var pen = new Pen(Color.Red, penWidth);
                using var font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.White);
                using var labelBrush = new SolidBrush(Color.FromArgb(215, 220, 38, 38));

                foreach (DefectBox box in boxes)
                {
                    Rectangle rect = ScaleBox(box, width, height);
                    if (rect.Width < 2 || rect.Height < 2) continue;

                    graphics.DrawRectangle(pen, rect);
                    SizeF labelSize = graphics.MeasureString(box.Label, font);
                    float labelY = Math.Max(0, rect.Y - labelSize.Height);

                    graphics.FillRectangle(labelBrush, rect.X, labelY, labelSize.Width + 6, labelSize.Height);
                    graphics.DrawString(box.Label, font, textBrush, rect.X + 3, labelY);
                }
            }

            string dir = Path.GetDirectoryName(imagePath) ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(imagePath);
            string annotatedPath = Path.Combine(dir, $"{stem}_defects_{DateTime.Now:HHmmss}.jpg");
            output.Save(annotatedPath, ImageFormat.Jpeg);
            return annotatedPath;
        }
        catch
        {
            return null;
        }
    }

    private static Rectangle ScaleBox(DefectBox box, int width, int height)
    {
        double max = new[] { box.X1, box.Y1, box.X2, box.Y2 }.Max(Math.Abs);

        double scaleX = max <= 1.01 ? width : max <= 1000.0 ? width / 1000.0 : 1.0;
        double scaleY = max <= 1.01 ? height : max <= 1000.0 ? height / 1000.0 : 1.0;

        int x1 = (int)Math.Round(box.X1 * scaleX);
        int y1 = (int)Math.Round(box.Y1 * scaleY);
        int x2 = (int)Math.Round(box.X2 * scaleX);
        int y2 = (int)Math.Round(box.Y2 * scaleY);

        int left = Math.Clamp(Math.Min(x1, x2), 0, width - 1);
        int top = Math.Clamp(Math.Min(y1, y2), 0, height - 1);
        int right = Math.Clamp(Math.Max(x1, x2), 0, width - 1);
        int bottom = Math.Clamp(Math.Max(y1, y2), 0, height - 1);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static string? ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;

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

    private sealed record DefectBox(string Label, double X1, double Y1, double X2, double Y2);
}
