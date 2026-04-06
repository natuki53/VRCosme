namespace VRCosme.Services.AI;

public readonly record struct SamModelDefinition(
    string Id,
    string EncoderFileName,
    string DecoderFileName,
    string EncoderDownloadUrl,
    string DecoderDownloadUrl,
    string DisplayNameKey
);

public static class SamModelCatalog
{
    // MobileSAM ONNX: AXERA-TECH/MobileSAM encoder + Acly/MobileSAM HQ decoder (Hugging Face)
    // NOTE: `vietanhdev/samexporter` has started returning 401 in some environments.
    private const string EncoderBaseUrl = "https://huggingface.co/AXERA-TECH/MobileSAM/resolve/main/onnx/";
    private const string DecoderBaseUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/";

    private static readonly SamModelDefinition[] Definitions =
    [
        new(
            "mobile_sam",
            "mobile_sam_encoder.onnx",
            "mobile_sam_decoder_hq.onnx",
            $"{EncoderBaseUrl}mobile_sam_encoder.onnx",
            $"{DecoderBaseUrl}sam_mask_decoder_multi.onnx",
            "SamSettings.Model.MobileSam"),
    ];

    public static IReadOnlyList<SamModelDefinition> GetAll() => Definitions;

    public static SamModelDefinition GetDefault() => Definitions[0];

    public static bool TryGetById(string? id, out SamModelDefinition definition)
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
