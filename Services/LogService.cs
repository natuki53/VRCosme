using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace VRCosme.Services;

/// <summary>簡易ファイルロガー</summary>
public static class LogService
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCosme");
    private static readonly string LogDir = Path.Combine(DataDir, "logs");
    private static readonly object Lock = new();
    private static bool _directoryEnsured;

    /// <summary>ログディレクトリを作成し、起動ログを記録</summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            _directoryEnsured = true;
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            Info($"VRCosme {version} 起動 ({Environment.OSVersion}, .NET {Environment.Version})");
            CleanupOldLogs();
        }
        catch
        {
            // ログ初期化失敗はアプリを止めない
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex != null)
            Write("ERROR", $"{message}\n{ex}");
        else
            Write("ERROR", message);
    }

    /// <summary>ログフォルダの全ログを ZIP に書き出す</summary>
    public static void ExportLog(string destinationPath)
    {
        if (!Directory.Exists(LogDir))
            throw new DirectoryNotFoundException("ログフォルダが見つかりません。");

        var logFiles = Directory.GetFiles(LogDir, "*.log");
        if (logFiles.Length == 0)
            throw new FileNotFoundException("書き出せるログファイルがありません。");

        // 既存ファイルがあれば削除 (SaveFileDialog で上書き確認済み)
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        using var zip = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (var logFile in logFiles)
        {
            // 書き込み中のログでも読めるよう FileShare.ReadWrite で開く
            var entryName = Path.GetFileName(logFile);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.CopyTo(entryStream);
        }
    }

    public static string GetLogDirectory() => LogDir;

    /// <summary>保持期間を超えた古いログファイルを削除</summary>
    private static void CleanupOldLogs(int retentionDays = 7)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;

            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in Directory.GetFiles(LogDir, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // クリーンアップ失敗はアプリを止めない
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
            var filePath = Path.Combine(LogDir, $"VRCosme_{DateTime.Now:yyyy-MM-dd}.log");

            lock (Lock)
            {
                if (!_directoryEnsured)
                {
                    Directory.CreateDirectory(LogDir);
                    _directoryEnsured = true;
                }
                File.AppendAllText(filePath, line);
            }
        }
        catch
        {
            // ログ書き込み失敗はアプリを止めない
        }
    }
}
