using System.IO;

namespace WenAgent;

public static class UserSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WenAgent");

    public static string DefaultWorkspace => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WenAgentWorkspace");

    public static string LoadWorkspace()
    {
        try
        {
            string file = Path.Combine(Dir, "workspace.txt");
            if (!File.Exists(file)) return DefaultWorkspace;

            string path = File.ReadAllText(file).Trim();
            return path.Length > 0 && Directory.Exists(path) ? path : DefaultWorkspace;
        }
        catch
        {
            return DefaultWorkspace;
        }
    }

    public static void SaveWorkspace(string path)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "workspace.txt"), path);
        }
        catch
        {
        }
    }
}
