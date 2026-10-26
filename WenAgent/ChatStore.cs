using System.IO;
using System.Text.Json;
using WenAgent.Agent;

namespace WenAgent;

public static class ChatStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WenAgent");

    private static string MessagesFile => Path.Combine(Dir, "chat-messages.json");
    private static string TurnsFile => Path.Combine(Dir, "chat-turns.json");

    public sealed record MessageDto(MessageKind Kind, string Header, string Content);

    public static List<ChatMessage> LoadMessages()
    {
        try
        {
            if (!File.Exists(MessagesFile)) return [];

            var messages = JsonSerializer.Deserialize<List<MessageDto>>(
                File.ReadAllText(MessagesFile), JsonOptions);

            if (messages == null) return [];

            return messages.TakeLast(100)
                .Select(m => new ChatMessage
                {
                    Kind = m.Kind,
                    Header = m.Header ?? "",
                    Content = m.Content ?? ""
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void SaveMessages(IEnumerable<ChatMessage> messages)
    {
        try
        {
            var items = messages
                .Where(m => m.Kind is not MessageKind.Info and not MessageKind.Streaming)
                .TakeLast(100)
                .Select(m => new MessageDto(
                    m.Kind,
                    m.Header,
                    m.Content.Length > 4000 ? m.Content[..4000] + "…" : m.Content))
                .ToList();

            Directory.CreateDirectory(Dir);
            File.WriteAllText(MessagesFile, JsonSerializer.Serialize(items, JsonOptions));
        }
        catch
        {
        }
    }

    public static void ClearMessages()
    {
        try { File.Delete(MessagesFile); } catch { }
    }

    public static List<ChatTurn> LoadTurns()
    {
        try
        {
            if (!File.Exists(TurnsFile)) return [];

            var turns = JsonSerializer.Deserialize<List<ChatTurn>>(
                File.ReadAllText(TurnsFile), JsonOptions);

            return turns?.TakeLast(20).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveTurns(IEnumerable<ChatTurn> turns)
    {
        try
        {
            var items = turns.TakeLast(20)
                .Select(t => new ChatTurn(
                    t.User.Length > 2000 ? t.User[..2000] : t.User,
                    t.Assistant.Length > 1000 ? t.Assistant[..1000] : t.Assistant))
                .ToList();

            Directory.CreateDirectory(Dir);
            File.WriteAllText(TurnsFile, JsonSerializer.Serialize(items, JsonOptions));
        }
        catch
        {
        }
    }

    public static void ClearTurns()
    {
        try { File.Delete(TurnsFile); } catch { }
    }
}
