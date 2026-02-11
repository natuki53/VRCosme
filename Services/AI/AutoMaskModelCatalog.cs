using VRCosme.Models;

namespace VRCosme.Services.AI;

public readonly record struct AutoMaskModelDefinition(
    string Id,
    string FileName,
    string DownloadUrl,
    string DisplayNameKey
);

public static class AutoMaskModelCatalog
{
    private const string ReleaseBaseUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/";
    private static readonly AutoMaskModelDefinition[] Definitions =
    [
        new(
            "u2net_human_seg",
            "u2net_human_seg.onnx",
            $"{ReleaseBaseUrl}u2net_human_seg.onnx",
            "AutoMaskSettings.Model.Human"),
        new(
            "isnet_general_use",
            "isnet-general-use.onnx",
            $"{ReleaseBaseUrl}isnet-general-use.onnx",
            "AutoMaskSettings.Model.Object"),
        new(
            "u2net",
            "u2net.onnx",
            $"{ReleaseBaseUrl}u2net.onnx",
            "AutoMaskSettings.Model.Background"),
        new(
            "isnet_anime",
            "isnet-anime.onnx",
            $"{ReleaseBaseUrl}isnet-anime.onnx",
            "AutoMaskSettings.Model.Other"),
        new(
            "silueta",
            "silueta.onnx",
            $"{ReleaseBaseUrl}silueta.onnx",
            "AutoMaskSettings.Model.Lightweight"),
    ];

    public static IReadOnlyList<AutoMaskModelDefinition> GetAll() => Definitions;

    public static AutoMaskModelDefinition GetForTarget(AutoMaskTargetKind target) => target switch
    {
        AutoMaskTargetKind.Human => Definitions[0],
        AutoMaskTargetKind.Object => Definitions[1],
        AutoMaskTargetKind.Background => Definitions[2],
        AutoMaskTargetKind.Other => Definitions[3],
        AutoMaskTargetKind.Lightweight => Definitions[4],
        _ => Definitions[1],
    };

    public static bool TryGetById(string? id, out AutoMaskModelDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            definition = default;
            return false;
        }

        for (int i = 0; i < Definitions.Length; i++)
        {
            if (string.Equals(Definitions[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                definition = Definitions[i];
                return true;
            }
        }

        definition = default;
        return false;
    }
}
