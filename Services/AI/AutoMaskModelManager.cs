using System.Net.Http;
using System.IO;

namespace VRCosme.Services.AI;

public static class AutoMaskModelManager
{
    private static readonly HttpClient DownloadClient = new();

    public static string ModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRCosme",
        "Models");

    public static string GetModelPath(AutoMaskModelDefinition definition) =>
        Path.Combine(ModelDirectory, definition.FileName);

    public static bool IsModelInstalled(AutoMaskModelDefinition definition)
    {
        var path = GetModelPath(definition);
        if (!File.Exists(path))
            return false;

        try
        {
            return new FileInfo(path).Length > 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    public static long GetModelSizeBytes(AutoMaskModelDefinition definition)
    {
        var path = GetModelPath(definition);
        if (!File.Exists(path))
            return 0;

        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    public static async Task<string> EnsureModelDownloadedAsync(
        AutoMaskModelDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var modelPath = GetModelPath(definition);
        if (IsModelInstalled(definition))
            return modelPath;

        Directory.CreateDirectory(ModelDirectory);
        var tempPath = modelPath + ".download";
        try
        {
            await using var response = await DownloadClient.GetStreamAsync(definition.DownloadUrl, cancellationToken);
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.CopyToAsync(output, cancellationToken);
            }

            if (File.Exists(modelPath))
                File.Delete(modelPath);

            File.Move(tempPath, modelPath);
            return modelPath;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore cleanup error
            }

            throw;
        }
    }

    public static void DeleteModel(AutoMaskModelDefinition definition)
    {
        var path = GetModelPath(definition);
        if (File.Exists(path))
            File.Delete(path);
    }
}
