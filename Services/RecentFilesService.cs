using System.IO;
using System.Text.Json;

namespace VRCosme.Services;

/// <summary>最近使ったファイルの永続化管理</summary>
public static class RecentFilesService
{
    private const int MaxRecentFiles = 10;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCosme");

    private static readonly string FilePath = Path.Combine(DataDir, "recent_files.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Add(string filePath)
    {
        try
        {
            var list = Load();
            list.Remove(filePath);
            list.Insert(0, filePath);
            if (list.Count > MaxRecentFiles)
                list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);

            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 保存失敗は無視
        }
    }
}
