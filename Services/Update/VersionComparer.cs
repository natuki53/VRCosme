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

        if (!TryParseSemanticVersion(currentRaw, out var current, out normalizedCurrent))
            return false;
        if (!TryParseSemanticVersion(latestRaw, out var latest, out normalizedLatest))
            return false;

        comparison = Compare(current, latest);
        return true;
    }

    public static bool TryParseVersion(string? raw, out Version version, out string normalized)
    {
        version = new Version(0, 0, 0, 0);
        normalized = "";

        if (!TryParseSemanticVersion(raw, out var semanticVersion, out normalized))
            return false;

        version = semanticVersion.CoreVersion;
        return true;
    }

    private static int Compare(SemanticVersion left, SemanticVersion right)
    {
        var coreComparison = CompareCore(left.CoreParts, right.CoreParts);
        if (coreComparison != 0)
            return coreComparison;

        var leftHasPrerelease = left.PrereleaseIdentifiers.Count > 0;
        var rightHasPrerelease = right.PrereleaseIdentifiers.Count > 0;

        if (!leftHasPrerelease && !rightHasPrerelease) return 0;
        if (!leftHasPrerelease) return 1;
        if (!rightHasPrerelease) return -1;

        var count = Math.Min(left.PrereleaseIdentifiers.Count, right.PrereleaseIdentifiers.Count);
        for (var i = 0; i < count; i++)
        {
            var cmp = ComparePrereleaseIdentifier(left.PrereleaseIdentifiers[i], right.PrereleaseIdentifiers[i]);
            if (cmp != 0)
                return cmp;
        }

        return left.PrereleaseIdentifiers.Count.CompareTo(right.PrereleaseIdentifiers.Count);
    }

    private static int CompareCore(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var max = Math.Max(left.Count, right.Count);
        for (var i = 0; i < max; i++)
        {
            var lv = i < left.Count ? left[i] : 0;
            var rv = i < right.Count ? right[i] : 0;
            var cmp = lv.CompareTo(rv);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsDigitsOnly(left);
        var rightNumeric = IsDigitsOnly(right);

        if (leftNumeric && rightNumeric)
        {
            var normalizedLeft = TrimLeadingZeros(left);
            var normalizedRight = TrimLeadingZeros(right);

            var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            if (lengthComparison != 0)
                return lengthComparison;

            return string.CompareOrdinal(normalizedLeft, normalizedRight);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;

        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseSemanticVersion(string? raw, out SemanticVersion version, out string normalized)
    {
        version = default;
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var value = raw.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        var metadataIndex = value.IndexOf('+');
        if (metadataIndex >= 0)
            value = value[..metadataIndex];

        var prereleaseIndex = value.IndexOf('-');
        var corePart = prereleaseIndex >= 0
            ? value[..prereleaseIndex]
            : value;
        var prereleasePart = prereleaseIndex >= 0
            ? value[(prereleaseIndex + 1)..]
            : "";

        if (!TryParseCore(corePart, out var coreParts))
            return false;

        var prereleaseIdentifiers = new List<string>();
        if (!string.IsNullOrEmpty(prereleasePart))
        {
            var identifiers = prereleasePart.Split('.');
            foreach (var id in identifiers)
            {
                var trimmed = id.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    return false;
                if (!trimmed.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
                    return false;
                prereleaseIdentifiers.Add(trimmed);
            }
        }

        var coreVersion = CreateVersion(coreParts);
        if (coreVersion == null)
            return false;

        var normalizedCore = string.Join(".", coreParts);
        normalized = prereleaseIdentifiers.Count > 0
            ? $"{normalizedCore}-{string.Join(".", prereleaseIdentifiers)}"
            : normalizedCore;

        version = new SemanticVersion(coreParts, prereleaseIdentifiers, coreVersion);
        return true;
    }

    private static bool TryParseCore(string rawCore, out List<int> coreParts)
    {
        coreParts = [];
        var tokens = rawCore.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > 4)
            return false;

        foreach (var token in tokens)
        {
            if (!IsDigitsOnly(token))
                return false;
            if (!int.TryParse(token, out var value) || value < 0)
                return false;
            coreParts.Add(value);
        }

        return coreParts.Count > 0;
    }

    private static Version? CreateVersion(IReadOnlyList<int> coreParts)
    {
        return coreParts.Count switch
        {
            1 => new Version(coreParts[0], 0),
            2 => new Version(coreParts[0], coreParts[1]),
            3 => new Version(coreParts[0], coreParts[1], coreParts[2]),
            4 => new Version(coreParts[0], coreParts[1], coreParts[2], coreParts[3]),
            _ => null
        };
    }

    private static bool IsDigitsOnly(string value) => value.All(char.IsDigit);

    private static string TrimLeadingZeros(string value)
    {
        var trimmed = value.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private readonly record struct SemanticVersion(
        IReadOnlyList<int> CoreParts,
        IReadOnlyList<string> PrereleaseIdentifiers,
        Version CoreVersion);
}
