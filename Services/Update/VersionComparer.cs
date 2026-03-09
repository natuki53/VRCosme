using System.Reflection;

namespace VRCosme.Services.Update;

public static class VersionComparer
{
    public static string GetCurrentVersionRaw()
    {
        return Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    public static bool TryCompare(
        string currentRaw,
        string latestRaw,
        out int comparison,
        out string normalizedCurrent,
        out string normalizedLatest)
    {
        comparison = 0;
        normalizedCurrent = "";
        normalizedLatest = "";

        if (!TryParseVersion(currentRaw, out var current, out normalizedCurrent))
            return false;
        if (!TryParseVersion(latestRaw, out var latest, out normalizedLatest))
            return false;

        comparison = current.CompareTo(latest);
        return true;
    }

    public static bool TryParseVersion(string? raw, out Version version, out string normalized)
    {
        version = new Version(0, 0, 0, 0);
        normalized = "";

        if (!TryNormalizeVersion(raw, out normalized))
            return false;

        if (!Version.TryParse(normalized, out var parsed) || parsed is null)
            return false;

        version = parsed;
        return true;
    }

    private static bool TryNormalizeVersion(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        var preReleaseIndex = value.IndexOf('-');
        if (preReleaseIndex >= 0)
            return false;

        var metadataIndex = value.IndexOf('+');
        if (metadataIndex >= 0)
            value = value[..metadataIndex];

        normalized = value.Trim();
        return !string.IsNullOrWhiteSpace(normalized);
    }
}
