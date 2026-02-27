using System.IO;
using System.Text.Json;
using VRCosme.Models;

namespace VRCosme.Services;

public static class SessionService
{
    private const int CurrentVersion = 1;
    private const string AppDirectoryName = "VRCosme";
    private const string SessionDirectoryName = "Sessions";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string GetDefaultSessionDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AppDirectoryName,
            SessionDirectoryName);

    public static string EnsureDefaultSessionDirectory()
    {
        var dir = GetDefaultSessionDirectory();
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Save(string sessionPath, SessionDocument document)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
            throw new ArgumentException("Session path is empty.", nameof(sessionPath));
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        var fullSessionPath = Path.GetFullPath(sessionPath);
        var fullSourcePath = Path.GetFullPath(document.SourceFilePath);

        document.Version = CurrentVersion;
        document.SourceFilePath = fullSourcePath;
        document.SourceFilePathRelative = BuildRelativeSourcePath(fullSessionPath, fullSourcePath);

        var dir = Path.GetDirectoryName(fullSessionPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(document, SerializerOptions);
        File.WriteAllText(fullSessionPath, json);
    }

    public static SessionDocument Load(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
            throw new ArgumentException("Session path is empty.", nameof(sessionPath));

        var fullSessionPath = Path.GetFullPath(sessionPath);
        var json = File.ReadAllText(fullSessionPath);
        var document = JsonSerializer.Deserialize<SessionDocument>(json, SerializerOptions)
            ?? throw new InvalidDataException("Invalid session file.");

        if (document.Version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Session version {document.Version} is newer than supported version {CurrentVersion}.");
        }

        document.SourceFilePath ??= "";
        document.SourceFilePathRelative ??= "";
        document.MaskLayers ??= [];
        return document;
    }

    public static string ResolveSourceImagePath(string sessionPath, SessionDocument document)
    {
        var absolutePath = document.SourceFilePath;
        if (!string.IsNullOrWhiteSpace(absolutePath))
        {
            var fullAbsolutePath = Path.GetFullPath(absolutePath);
            if (File.Exists(fullAbsolutePath))
                return fullAbsolutePath;
        }

        if (!string.IsNullOrWhiteSpace(document.SourceFilePathRelative))
        {
            var sessionDir = Path.GetDirectoryName(Path.GetFullPath(sessionPath)) ?? "";
            var relativeCandidate = Path.GetFullPath(Path.Combine(sessionDir, document.SourceFilePathRelative));
            if (File.Exists(relativeCandidate))
                return relativeCandidate;
        }

        return "";
    }

    private static string BuildRelativeSourcePath(string sessionPath, string sourceFilePath)
    {
        var sessionDir = Path.GetDirectoryName(sessionPath);
        if (string.IsNullOrWhiteSpace(sessionDir))
            return "";

        try
        {
            var relative = Path.GetRelativePath(sessionDir, sourceFilePath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? "" : relative;
        }
        catch
        {
            return "";
        }
    }
}
