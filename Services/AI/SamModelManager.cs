using System.IO;
using System.Net.Http;

namespace VRCosme.Services.AI;

public static class SamModelManager
{
    private static readonly HttpClient DownloadClient = new();

    public static string ModelDirectory { get; } = AutoMaskModelManager.ModelDirectory;

    public static string GetEncoderPath(SamModelDefinition definition) =>
        Path.Combine(ModelDirectory, definition.EncoderFileName);

    public static string GetDecoderPath(SamModelDefinition definition) =>
        Path.Combine(ModelDirectory, definition.DecoderFileName);

    public static bool IsModelInstalled(SamModelDefinition definition) =>
        IsFileInstalled(GetEncoderPath(definition)) && IsFileInstalled(GetDecoderPath(definition));

    public static (long EncoderBytes, long DecoderBytes) GetModelSizeBytes(SamModelDefinition definition) =>
        (GetFileSizeBytes(GetEncoderPath(definition)), GetFileSizeBytes(GetDecoderPath(definition)));

    private static bool IsFileInstalled(string path)
    {
        if (!File.Exists(path)) return false;
        try { return new FileInfo(path).Length > 1024 * 1024; }
        catch { return false; }
    }

    private static long GetFileSizeBytes(string path)
    {
        if (!File.Exists(path)) return 0;
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public static async Task EnsureModelDownloadedAsync(
        SamModelDefinition definition,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        await DownloadFileIfMissingAsync(
            definition.EncoderDownloadUrl,
            GetEncoderPath(definition),
            cancellationToken);
        await DownloadFileIfMissingAsync(
            definition.DecoderDownloadUrl,
            GetDecoderPath(definition),
            cancellationToken);
    }

    private static async Task DownloadFileIfMissingAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (IsFileInstalled(targetPath))
            return;

        var tempPath = targetPath + ".download";
        try
        {
            await using var response = await DownloadClient.GetStreamAsync(url, cancellationToken);
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.CopyToAsync(output, cancellationToken);
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tempPath, targetPath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* ignore cleanup error */ }
            throw;
        }
    }

    public static void DeleteModel(SamModelDefinition definition)
    {
        TryDelete(GetEncoderPath(definition));
        TryDelete(GetDecoderPath(definition));
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
