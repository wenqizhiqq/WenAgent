using System.IO;

namespace WenAgent.Agent;

public static class VisionModelLocator
{
    public const string PreferredModel =
        @"D:\wqz\models\qwen2.5-vl-3b-instruct-q4_k_m.gguf";

    public const string PreferredMmproj =
        @"D:\wqz\models\mmproj-qwen2.5-vl-3b-instruct-q8_0.gguf";

    public const string DownloadPage =
        "https://modelscope.cn/models/ggml-org/Qwen2.5-VL-3B-Instruct-GGUF";

    public static (string? ModelPath, string? MmprojPath) Resolve()
    {
        if (File.Exists(PreferredModel) && File.Exists(PreferredMmproj))
            return (PreferredModel, PreferredMmproj);

        string dir = Path.GetDirectoryName(PreferredModel) ?? @"D:\wqz\models";

        string? model = Find(dir, name =>
            name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && name.Contains("vl", StringComparison.OrdinalIgnoreCase)
            && name.Contains("3b", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase));

        string? mmproj = Find(dir, name =>
            name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
            && name.Contains("vl", StringComparison.OrdinalIgnoreCase));

        return (model, mmproj);
    }

    public static string MissingMessage()
        => $"""
            未找到本地视觉模型。请把以下两个文件放到 D:\wqz\models：
            - qwen2.5-vl-3b-instruct-q4_k_m.gguf
            - mmproj-qwen2.5-vl-3b-instruct-q8_0.gguf

            下载页：{DownloadPage}
            """;

    private static string? Find(string dir, Func<string, bool> predicate)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.gguf")
                .FirstOrDefault(path => predicate(Path.GetFileName(path)));
        }
        catch
        {
            return null;
        }
    }
}
